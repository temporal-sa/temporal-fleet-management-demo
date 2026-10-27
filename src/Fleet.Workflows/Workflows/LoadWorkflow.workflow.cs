using System.Text.Json;
using Fleet.Core;
using Fleet.Workflows.Activities;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Fleet.Workflows;

/// <summary>
/// One workflow per load, from tender to delivery.
///
/// Planning asks the recommender for candidates, then reserves the truck and the driver through their
/// entity workflows. If the driver rejects after the truck was reserved, the truck hold is released (a saga
/// compensation). TMS milestones are recorded in order, exactly once, even across a TMS outage.
/// If the truck breaks down, the maintenance case signals this workflow and the load is re-powered
/// with another truck, so nothing downstream fails.
/// </summary>
[Workflow]
public class LoadWorkflow
{
    private readonly LoadState state;
    private readonly DemoSettings settings;
    private readonly string? preferredUnit;
    private bool arrivedAtPickup;
    private bool loaded;
    private bool arrivedAtDelivery;
    private bool unloaded;
    private bool repowerRequested;
    private bool capacityNudged;
    private TruckUnavailableNotice? repowerNotice;

    [WorkflowInit]
    public LoadWorkflow(LoadInput input)
    {
        settings = input.Settings;
        preferredUnit = input.PreferredUnit;
        state = new LoadState
        {
            LoadId = input.LoadId,
            Shipper = input.Shipper,
            Commodity = input.Commodity,
            OriginHub = input.OriginHub,
            DestinationHub = input.DestinationHub,
            Hazmat = input.Hazmat,
            Rate = input.Rate,
            RouteMiles = input.RouteMiles,
        };
    }

    [WorkflowQuery]
    public LoadState GetState() => state;

    [WorkflowRun]
    public async Task<LoadResult> RunAsync(LoadInput input)
    {
        state.StatusSince = Workflow.UtcNow;
        FleetVisibility.Upsert(settings, 
            FleetSearchAttributes.Entity.ValueSet("Load"),
            FleetSearchAttributes.Status.ValueSet(state.Status.ToString()),
            FleetSearchAttributes.Unit.ValueSet("none"),
            FleetSearchAttributes.NeedsAttention.ValueSet(false));

        var origin = RoadNetwork.Get(state.OriginHub);
        var destination = RoadNetwork.Get(state.DestinationHub);
        Timeline("info",
            $"Tendered by {state.Shipper}: {state.Commodity}{(state.Hazmat ? " (hazmat)" : "")}, " +
            $"{origin.Label} to {destination.Label}, {state.RouteMiles:0} mi, ${state.Rate:N0}.");
        await MilestoneAsync("TENDERED");

        var nextPreferred = preferredUnit;
        while (true)
        {
            await PlanAsync(nextPreferred);
            nextPreferred = null;
            await DispatchAsync();
            if (await RunTripAsync())
            {
                break;
            }

            await RepowerAsync();
        }

        await MilestoneAsync("DELIVERED");
        await ReleaseAssignmentAsync(delivered: true);

        var payload = JsonSerializer.Serialize(new
        {
            loadId = state.LoadId,
            shipper = state.Shipper,
            rate = state.Rate,
            miles = state.RouteMiles,
            unit = state.AssignedUnit,
            driverId = state.AssignedDriverId,
            detention = state.DetentionStarted,
            repowers = state.RepowerCount,
        });
        await PublishAsync("billing.load-delivered", payload);
        SetStatus(LoadStatus.Delivered);
        Timeline("success", "Delivered. Published billing.load-delivered for the existing invoice pipeline.");

        await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
        return new LoadResult(state.LoadId, "Delivered", state.AssignedUnit, state.RepowerCount, state.DetentionStarted);
    }

    // ---------------------------------------------------------------- signals (geofence events from the ELD)

    [WorkflowSignal(Signals.ArrivedAtPickup)]
    public async Task ArrivedAtPickupAsync(GeofenceEvent e)
    {
        if (IsAssigned(e))
        {
            arrivedAtPickup = true;
            Timeline("signal", $"Geofence: arrived at {e.StopName}.");
        }
    }

    [WorkflowSignal(Signals.Loaded)]
    public async Task LoadedAsync(GeofenceEvent e)
    {
        if (IsAssigned(e))
        {
            loaded = true;
            Timeline("signal", "Loaded and departing.");
        }
    }

    [WorkflowSignal(Signals.ArrivedAtDelivery)]
    public async Task ArrivedAtDeliveryAsync(GeofenceEvent e)
    {
        if (IsAssigned(e))
        {
            arrivedAtDelivery = true;
            Timeline("signal", $"Geofence: arrived at {e.StopName}.");
        }
    }

    [WorkflowSignal(Signals.Unloaded)]
    public async Task UnloadedAsync(GeofenceEvent e)
    {
        if (IsAssigned(e))
        {
            unloaded = true;
            Timeline("signal", "Unloaded. Consignee signed the bill of lading.");
        }
    }

    /// <summary>Sent by a maintenance case when the assigned truck is taken out of service.</summary>
    [WorkflowSignal(Signals.TruckUnavailable)]
    public async Task TruckUnavailableAsync(TruckUnavailableNotice notice)
    {
        if (notice.Unit != state.AssignedUnit || state.Status is LoadStatus.AtDelivery or LoadStatus.Delivered)
        {
            Timeline("info", $"Notice about unit {notice.Unit} ignored; it is not the active truck for this load.");
            return;
        }

        repowerNotice = notice;
        repowerRequested = true;
        Timeline("signal", $"Maintenance case {notice.MaintenanceId} reports unit {notice.Unit} out of service.");
    }

    [WorkflowSignal(Signals.CapacityChanged)]
    public async Task CapacityChangedAsync()
    {
        capacityNudged = true;
    }

    // ---------------------------------------------------------------- steps

    private async Task PlanAsync(string? preferred)
    {
        SetStatus(LoadStatus.Planning);
        var pickup = PickupPoint();
        var driveMinutes = state.RouteMiles / 55.0 * 60.0;

        while (true)
        {
            state.PlanningAttempts++;
            var request = new AssignmentRequest(
                state.LoadId, pickup.Lat, pickup.Lon, state.Hazmat, driveMinutes, state.PreviousUnits.ToList(), preferred);
            preferred = null;

            var candidates = await Workflow.ExecuteActivityAsync(
                (DispatchActivities a) => a.RecommendAssignmentsAsync(request),
                ActivityProfiles.Local());

            Plan("info", candidates.Count == 0
                ? "Recommender found no available truck and driver pairs."
                : $"Recommender returned {candidates.Count} candidates: {string.Join(", ", candidates.Select(c => c.Unit))}.");

            foreach (var candidate in candidates)
            {
                if (await TryAssignAsync(candidate, driveMinutes))
                {
                    return;
                }
            }

            SetStatus(LoadStatus.WaitingForCapacity);
            if (state.PlanningAttempts >= 3 && !state.NeedsAttention)
            {
                state.NeedsAttention = true;
                UpsertAttention();
                Timeline("warn", "No capacity after 3 attempts. Flagged for a dispatcher.");
            }

            capacityNudged = false;
            await Workflow.WaitConditionAsync(() => capacityNudged, settings.Real(45));
            SetStatus(LoadStatus.Planning);
        }
    }

    private async Task<bool> TryAssignAsync(AssignmentCandidate candidate, double driveMinutes)
    {
        var truckRequest = new ReserveTruckRequest(state.LoadId, candidate.DriverId);
        var unit = candidate.Unit;
        try
        {
            await Workflow.ExecuteActivityAsync(
                (EntityActivities a) => a.ReserveTruckAsync(unit, truckRequest),
                ActivityProfiles.Entity());
        }
        catch (ActivityFailureException e)
        {
            Plan("rejected", $"Unit {unit} declined: {Reason(e)}");
            return false;
        }

        var driverId = candidate.DriverId;
        var endorsements = state.Hazmat ? new List<string> { "Hazmat" } : new List<string>();
        var driverRequest = new ReserveDriverRequest(state.LoadId, unit, endorsements, driveMinutes);
        try
        {
            await Workflow.ExecuteActivityAsync(
                (EntityActivities a) => a.ReserveDriverAsync(driverId, driverRequest),
                ActivityProfiles.Entity());
        }
        catch (ActivityFailureException e)
        {
            Plan("rejected", $"Driver {candidate.DriverName} declined: {Reason(e)}");

            // Saga compensation: undo the truck reservation we just made.
            var release = new ReleaseTruckRequest(state.LoadId, false);
            await Workflow.ExecuteActivityAsync(
                (EntityActivities a) => a.ReleaseTruckAsync(unit, release),
                ActivityProfiles.Entity());
            Plan("compensation", $"Released the hold on unit {unit}.");
            return false;
        }

        state.AssignedUnit = unit;
        state.AssignedDriverId = driverId;
        state.AssignedDriverName = candidate.DriverName;
        state.NeedsAttention = false;
        Plan("success", $"Assigned unit {unit} with {candidate.DriverName}. {candidate.Rationale}");
        SetStatus(LoadStatus.Assigned);
        FleetVisibility.Upsert(settings, 
            FleetSearchAttributes.Unit.ValueSet(unit),
            FleetSearchAttributes.NeedsAttention.ValueSet(false));
        await MilestoneAsync("ASSIGNED");
        return true;
    }

    private async Task DispatchAsync()
    {
        var pickup = PickupPoint();
        var origin = RoadNetwork.Get(state.OriginHub);
        var destination = RoadNetwork.Get(state.DestinationHub);
        var roadside = state.RoadsideTransferPoint != null;

        var stops = new List<DispatchStop>
        {
            new(roadside ? "Roadside trailer transfer" : $"{state.Shipper}, {origin.Label}",
                pickup.Lat, pickup.Lon, "pickup", Signals.ArrivedAtPickup, Signals.Loaded, roadside ? 30 : 0, false),
            new($"Consignee, {destination.Label}",
                destination.Lat, destination.Lon, "delivery", Signals.ArrivedAtDelivery, Signals.Unloaded, 0, true),
        };

        var dispatch = new DispatchRequest(
            state.AssignedUnit!, state.AssignedDriverId, Workflow.Info.WorkflowId, $"Load {state.LoadId}", false, stops);
        await Workflow.ExecuteActivityAsync(
            (DispatchActivities a) => a.DispatchRouteAsync(dispatch),
            ActivityProfiles.Local());

        var unit = state.AssignedUnit!;
        var loadId = state.LoadId;
        await Workflow.ExecuteActivityAsync(
            (EntityActivities a) => a.MarkTruckDispatchedAsync(unit, loadId),
            ActivityProfiles.Entity());

        arrivedAtPickup = false;
        loaded = false;
        arrivedAtDelivery = false;
        unloaded = false;
        Timeline("info", $"Route sent to unit {unit}'s ELD.");
        await MilestoneAsync("DISPATCHED");
        SetStatus(LoadStatus.EnRouteToPickup);
    }

    /// <summary>Returns true when delivered, false when the truck must be replaced.</summary>
    private async Task<bool> RunTripAsync()
    {
        await Workflow.WaitConditionAsync(() => arrivedAtPickup || repowerRequested);
        if (repowerRequested)
        {
            return false;
        }

        state.ArrivedPickupAt = Workflow.UtcNow;
        SetStatus(LoadStatus.AtPickup);
        await MilestoneAsync("ARRIVED_PICKUP");

        // Two hours of free time at the shipper, then detention starts. A durable timer, not a cron job.
        var loadedInFreeTime = await Workflow.WaitConditionAsync(() => loaded || repowerRequested, settings.Real(120));
        if (repowerRequested)
        {
            return false;
        }

        if (!loadedInFreeTime && state.RoadsideTransferPoint == null)
        {
            state.DetentionStarted = true;
            Timeline("warn", "Shipper kept the truck past 2 hours of free time. Detention started; billing notified.");
            var payload = JsonSerializer.Serialize(new { loadId = state.LoadId, shipper = state.Shipper, unit = state.AssignedUnit });
            await PublishAsync("billing.detention-started", payload);
            await Workflow.WaitConditionAsync(() => loaded || repowerRequested);
            if (repowerRequested)
            {
                return false;
            }
        }

        state.LoadedAt = Workflow.UtcNow;
        await MilestoneAsync("LOADED");
        SetStatus(LoadStatus.InTransit);

        await Workflow.WaitConditionAsync(() => arrivedAtDelivery || repowerRequested);
        if (repowerRequested)
        {
            return false;
        }

        SetStatus(LoadStatus.AtDelivery);
        await MilestoneAsync("ARRIVED_DELIVERY");
        await Workflow.WaitConditionAsync(() => unloaded);
        state.DeliveredAt = Workflow.UtcNow;
        return true;
    }

    private async Task RepowerAsync()
    {
        var notice = repowerNotice!;
        repowerRequested = false;
        state.RepowerCount++;
        state.RepowerReason = notice.Reason;
        SetStatus(LoadStatus.Repowering);
        Timeline("warn", $"Unit {notice.Unit} is down ({notice.Reason}). Re-powering this load with another truck.");
        await MilestoneAsync("REPOWER");

        var wasLoaded = state.LoadedAt != null;
        await ReleaseAssignmentAsync(delivered: false);
        if (state.AssignedUnit != null)
        {
            state.PreviousUnits.Add(state.AssignedUnit);
        }

        if (wasLoaded)
        {
            state.RoadsideTransferPoint = new GeoPoint(notice.Lat, notice.Lon);
            Timeline("info", "The trailer is already loaded. The replacement truck will pick it up at the breakdown location.");
        }

        state.AssignedUnit = null;
        state.AssignedDriverId = null;
        state.AssignedDriverName = null;
        state.LoadedAt = null;
        state.ArrivedPickupAt = null;
        arrivedAtPickup = false;
        loaded = false;
        arrivedAtDelivery = false;
        unloaded = false;
    }

    private async Task ReleaseAssignmentAsync(bool delivered)
    {
        var loadId = state.LoadId;
        if (state.AssignedUnit != null)
        {
            var unit = state.AssignedUnit;
            var release = new ReleaseTruckRequest(loadId, delivered);
            await Workflow.ExecuteActivityAsync(
                (EntityActivities a) => a.ReleaseTruckAsync(unit, release),
                ActivityProfiles.Entity());
        }

        if (state.AssignedDriverId != null)
        {
            var driverId = state.AssignedDriverId;
            await Workflow.ExecuteActivityAsync(
                (EntityActivities a) => a.ReleaseDriverAsync(driverId, loadId),
                ActivityProfiles.Entity());
        }
    }

    // ---------------------------------------------------------------- helpers

    private GeoPoint PickupPoint() =>
        state.RoadsideTransferPoint ?? RoadNetwork.Get(state.OriginHub).Point;

    private bool IsAssigned(GeofenceEvent e)
    {
        if (e.Unit == state.AssignedUnit)
        {
            return true;
        }

        Timeline("info", $"Geofence event from unit {e.Unit} ignored; it is no longer assigned.");
        return false;
    }

    /// <summary>
    /// Records a TMS milestone. Sequential and awaited on purpose: during a TMS outage this step
    /// retries, and when the TMS returns the milestones land in order, each exactly once.
    /// </summary>
    private async Task MilestoneAsync(string milestone)
    {
        var record = new LoadMilestone(
            state.LoadId, milestone, state.AssignedUnit, state.AssignedDriverId, $"{state.LoadId}:{milestone}:{state.RepowerCount}");
        await Workflow.ExecuteActivityAsync(
            (TmsActivities a) => a.RecordLoadMilestoneAsync(record),
            ActivityProfiles.Integration(settings));
        Timeline("integration", $"TMS milestone {milestone} recorded.");
    }

    private async Task PublishAsync(string topic, string payload)
    {
        var evt = new DownstreamEvent(topic, state.LoadId, payload);
        await Workflow.ExecuteActivityAsync(
            (DispatchActivities a) => a.PublishEventAsync(evt),
            ActivityProfiles.Local());
    }

    private void SetStatus(LoadStatus status)
    {
        if (state.Status == status)
        {
            return;
        }

        state.Status = status;
        state.StatusSince = Workflow.UtcNow;
        FleetVisibility.Upsert(settings, FleetSearchAttributes.Status.ValueSet(status.ToString()));
    }

    private void UpsertAttention() =>
        FleetVisibility.Upsert(settings, FleetSearchAttributes.NeedsAttention.ValueSet(state.NeedsAttention));

    private void Timeline(string kind, string text) =>
        state.Timeline.AddCapped(new EntityEvent(Workflow.UtcNow, kind, text), 60);

    private void Plan(string kind, string text)
    {
        state.PlanningLog.AddCapped(new EntityEvent(Workflow.UtcNow, kind, text), 40);
        Timeline(kind, text);
    }

    private static string Reason(ActivityFailureException e) => e.InnerException?.Message ?? e.Message;
}
