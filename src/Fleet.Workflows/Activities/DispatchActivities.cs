using Fleet.Core;
using Fleet.Integrations;
using Temporalio.Activities;
using Temporalio.Client;

namespace Fleet.Workflows.Activities;

public class DispatchActivities
{
    private readonly ITemporalClient client;
    private readonly TelematicsClient telematics;
    private readonly KafkaClient kafka;

    public DispatchActivities(ITemporalClient client, TelematicsClient telematics, KafkaClient kafka)
    {
        this.client = client;
        this.telematics = telematics;
        this.kafka = kafka;
    }

    /// <summary>
    /// Ranks truck and driver pairs for a load. Uses a visibility query on the FleetStatus search attribute
    /// to find available trucks, then queries each truck and driver entity for live state. Visibility is
    /// eventually consistent, so this is a recommendation only; the entities' update validators decide.
    /// </summary>
    [Activity]
    public async Task<List<AssignmentCandidate>> RecommendAssignmentsAsync(AssignmentRequest request)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var fleet = await ExternalCall.RunAsync(() => telematics.GetFleetAsync(ct));
        var positions = fleet.Trucks.ToDictionary(t => t.Unit);

        var units = new List<string>();
        try
        {
            await foreach (var wf in client.ListWorkflowsAsync(
                               "WorkflowType = 'TruckWorkflow' AND ExecutionStatus = 'Running' AND FleetStatus = 'Available'"))
            {
                units.Add(wf.Id.Replace("truck-", "", StringComparison.Ordinal));
            }
        }
        catch (Exception)
        {
            // Search attributes not registered. Fall back to every truck the telematics feed knows about.
            units = positions.Keys.ToList();
        }

        if (request.PreferredUnit != null)
        {
            units.Remove(request.PreferredUnit);
            units.Insert(0, request.PreferredUnit);
        }

        var lookups = units
            .Where(u => !request.ExcludeUnits.Contains(u) && positions.ContainsKey(u))
            .Select(u => EvaluateAsync(u, positions[u], request));
        var evaluated = await Task.WhenAll(lookups);

        return evaluated
            .Where(c => c != null)
            .Select(c => c!)
            .OrderByDescending(c => c.DispatcherOverride)
            .ThenBy(c => c.Score)
            .Take(6)
            .ToList();
    }

    private async Task<AssignmentCandidate?> EvaluateAsync(string unit, TelematicsTruckDto position, AssignmentRequest request)
    {
        var isOverride = unit == request.PreferredUnit;
        var truck = await TryQueryAsync<TruckWorkflow, TruckState>(WorkflowIds.Truck(unit), wf => wf.GetState());
        if (truck == null || (!isOverride && truck.Status != TruckStatus.Available))
        {
            return null;
        }

        var driverId = position.DriverId
                       ?? FleetSeed.Trucks.FirstOrDefault(t => t.Unit == unit)?.PairedDriverId
                       ?? FleetSeed.Drivers.FirstOrDefault(d => d.PairedUnit == unit)?.DriverId;
        if (driverId == null)
        {
            return null;
        }

        var driver = await TryQueryAsync<DriverWorkflow, DriverState>(WorkflowIds.Driver(driverId), wf => wf.GetState());
        if (driver == null)
        {
            return null;
        }

        // Employment status is deliberately not filtered here: the driver entity owns that rule.
        if (!isOverride && request.Hazmat && !driver.Endorsements.Contains("Hazmat"))
        {
            return null;
        }

        var deadhead = Math.Round(Geo.DistanceMiles(position.Lat, position.Lon, request.PickupLat, request.PickupLon) * RoadNetwork.RoadFactor, 0);
        var hos = position.Hos ?? driver.Hos;
        var hoursLeft = hos.DriveMinutesLeft / 60.0;
        var pmSoon = truck.PmDueOdometer - position.Telemetry.Odometer < 600;
        var score = deadhead + (hos.DriveMinutesLeft < 180 ? 150 : 0) + (pmSoon ? 90 : 0);
        var rationale = $"{deadhead:0} mi deadhead, {hoursLeft:0.0} h drive time left{(pmSoon ? ", PM due soon" : "")}.";
        if (isOverride)
        {
            rationale = "Dispatcher override. " + rationale;
        }

        return new AssignmentCandidate(unit, driverId, driver.Name, deadhead, hos.DriveMinutesLeft, score, rationale, isOverride);
    }

    private async Task<TResult?> TryQueryAsync<TWorkflow, TResult>(
        string workflowId, System.Linq.Expressions.Expression<Func<TWorkflow, TResult>> query)
        where TResult : class
    {
        try
        {
            return await client.GetWorkflowHandle<TWorkflow>(workflowId).QueryAsync(query);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [Activity]
    public Task DispatchRouteAsync(DispatchRequest request)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => telematics.DispatchAsync(request, ct));
    }

    [Activity]
    public Task PublishEventAsync(DownstreamEvent evt)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => kafka.PublishAsync(evt, ct));
    }
}
