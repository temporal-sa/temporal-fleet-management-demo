using Fleet.Core;

namespace Fleet.Api.Services;

public record ScenarioRequest(string? Unit, string? DriverId);

public record ScenarioResult(bool Ok, string Message, string? Unit = null, string? DriverId = null, string? WorkflowId = null);

/// <summary>
/// Triggers the demo scenarios. Almost all of them act on the outside world (inject a fault into the
/// telematics stream, hire someone in Salesforce, edit a record in the TMS) and let the workflows react,
/// exactly as they would in production.
/// </summary>
public class ScenarioRunner
{
    private readonly FleetStateCache cache;
    private readonly ExternalSystemsProxy external;
    private readonly LoadTenderer tenderer;
    private readonly TemporalClientProvider temporal;
    private readonly Random rng = new();

    public ScenarioRunner(FleetStateCache cache, ExternalSystemsProxy external, LoadTenderer tenderer, TemporalClientProvider temporal)
    {
        this.cache = cache;
        this.external = external;
        this.tenderer = tenderer;
        this.temporal = temporal;
    }

    public async Task<ScenarioResult> RunAsync(string name, ScenarioRequest request)
    {
        var snapshot = cache.Current;
        switch (name)
        {
            case "engine-anomaly":
                return await InjectAsync(request.Unit ?? PickLoadedAndDriving(snapshot) ?? PickDriving(snapshot), "engine-overheat",
                    "Coolant temperature will climb past 228°F in a few seconds. The stream rule fires and the truck entity opens a case.");
            case "engine-approval":
            {
                var unit = request.Unit ?? PickFarFromPartnerEngineWorkshop(snapshot);
                if (unit == null)
                {
                    return new ScenarioResult(false,
                        "No idle truck is far enough from a partner engine workshop right now. Try again in a minute, or use Overheat engine on a truck near Denver, Memphis or El Paso.");
                }

                return await InjectAsync(unit, "engine-overheat",
                    "Far from any partner engine workshop, so the best option in range is a general workshop above the approval threshold. Watch the inbox.");
            }

            case "tire-leak":
                return await InjectAsync(request.Unit ?? PickLoadedAndDriving(snapshot) ?? PickDriving(snapshot), "tire-leak",
                    "Slow leak. Medium severity, so the repair waits for the current delivery.");
            case "def-fault":
                return await InjectAsync(request.Unit ?? PickDriving(snapshot), "def-fault", "DEF level drops below 5%.");
            case "pm-due":
                return await InjectAsync(request.Unit ?? PickAny(snapshot, TruckStatus.Available, TruckStatus.OnLoad), "pm-due",
                    "Odometer passes the PM-A interval. Low severity; routed to a partner workshop.");
            case "breakdown":
                return await InjectAsync(request.Unit ?? PickLoadedAndDriving(snapshot, inTransitOnly: true) ?? PickLoadedAndDriving(snapshot), "breakdown",
                    "Engine shutdown on the shoulder. Watch the load re-power and the tow.");
            case "tire-blowout":
                return await InjectAsync(request.Unit ?? PickLoadedAndDriving(snapshot), "tire-blowout", "Tire failure. Roadside response.");
            case "hard-brake":
            {
                var unit = request.Unit ?? PickDriving(snapshot);
                if (unit == null)
                {
                    return new ScenarioResult(false, "No truck is driving right now.");
                }

                var (status, body) = await external.PostAsync("/telematics/inject-safety", new { unit, type = "HardBrake" });
                var driverId = body?["driverId"]?.ToString();
                return status < 300
                    ? new ScenarioResult(true, "Hard-brake event sent. The driver is driving, so the coaching call is held until the next stop.", unit, driverId,
                        driverId != null ? WorkflowIds.Driver(driverId) : null)
                    : new ScenarioResult(false, body?.ToJsonString() ?? "failed", unit);
            }

            case "driver-break":
            {
                var driverId = request.DriverId ??
                               snapshot.Drivers.FirstOrDefault(d => d.State?.PendingSafetyEvents.Count > 0)?.State?.DriverId ??
                               snapshot.Drivers.FirstOrDefault(d => d.State?.Duty == DutyStatus.Driving)?.State?.DriverId;
                if (driverId == null)
                {
                    return new ScenarioResult(false, "No driver is on the road right now.");
                }

                var (status, body) = await external.PostAsync($"/telematics/drivers/{driverId}/break", new { minutes = 30 });
                return status < 300
                    ? new ScenarioResult(true, $"Driver {driverId} pulled into a rest area. The ELD reports off duty and any held coaching call starts.",
                        body?["unit"]?.ToString(), driverId, WorkflowIds.Driver(driverId))
                    : new ScenarioResult(false, body?.ToJsonString() ?? "failed");
            }

            case "new-hire":
            {
                var (status, body) = await external.PostAsync("/admin/salesforce/hire", new { homeHub = "OMA" });
                var driverId = body?["Driver_Id__c"]?.ToString();
                return status < 300
                    ? new ScenarioResult(true, $"Hired {body?["Name"]} in Salesforce. The change event starts an onboarding workflow.", null, driverId,
                        driverId != null ? WorkflowIds.Driver(driverId) : null)
                    : new ScenarioResult(false, body?.ToJsonString() ?? "failed");
            }

            case "assign-to-shop-truck":
            {
                var unit = request.Unit ?? snapshot.Trucks.FirstOrDefault(t =>
                    t.Entity?.OpenMaintenanceId != null || t.Entity?.Status is TruckStatus.InShop or TruckStatus.ShopBound or TruckStatus.Breakdown)?.Unit;
                if (unit == null)
                {
                    return new ScenarioResult(false, "No truck has an open repair right now. Run a maintenance scenario first.");
                }

                var input = await tenderer.TenderAsync(new TenderRequest(null, null, false, unit));
                return new ScenarioResult(true,
                    $"Load {input.LoadId} tendered with a dispatcher override for unit {unit}. The truck entity will refuse it; planning moves on.",
                    unit, null, WorkflowIds.Load(input.LoadId));
            }

            case "tms-conflict":
            {
                var unit = request.Unit ?? PickAny(snapshot, TruckStatus.InShop, TruckStatus.ShopBound, TruckStatus.OnLoad, TruckStatus.Breakdown);
                if (unit == null)
                {
                    return new ScenarioResult(false, "No suitable truck found.");
                }

                var (status, _) = await external.PostAsync($"/tms/admin/inject-conflict/{unit}");
                if (status >= 300)
                {
                    return new ScenarioResult(false, "Could not edit the TMS record.", unit);
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
                await TriggerSyncAsync();
                return new ScenarioResult(true,
                    $"Unit {unit} was edited by hand in the TMS. The reconciliation run is starting now; watch the truck's timeline.",
                    unit, null, WorkflowIds.Truck(unit));
            }

            case "run-sync":
                await TriggerSyncAsync();
                return new ScenarioResult(true, "Equipment sync triggered.");

            case "tender-load":
            {
                var input = await tenderer.TenderAsync(new TenderRequest(null, null, null, null));
                return new ScenarioResult(true, $"Load {input.LoadId} tendered.", null, null, WorkflowIds.Load(input.LoadId));
            }

            case "expire-salesforce-tokens":
                await external.PostAsync("/admin/salesforce/expire-tokens");
                return new ScenarioResult(true, "Salesforce sessions expired. The next call gets a 401, re-authenticates and replays.");

            default:
                return new ScenarioResult(false, $"Unknown scenario '{name}'.");
        }
    }

    private async Task<ScenarioResult> InjectAsync(string? unit, string kind, string message)
    {
        if (unit == null)
        {
            return new ScenarioResult(false, "No suitable truck found for this scenario right now.");
        }

        var (status, body) = await external.PostAsync("/telematics/inject", new { unit, kind });
        return status < 300
            ? new ScenarioResult(true, $"Unit {unit}: {message}", unit, null, WorkflowIds.Truck(unit))
            : new ScenarioResult(false, body?.ToJsonString() ?? "failed", unit);
    }

    private async Task TriggerSyncAsync()
    {
        var client = await temporal.GetAsync();
        await client.GetScheduleHandle(WorkflowIds.EquipmentSyncSchedule).TriggerAsync();
    }

    private string? PickLoadedAndDriving(FleetSnapshot s, bool inTransitOnly = false)
    {
        var loadsInTransit = s.Loads
            .Where(l => l.ExecutionStatus == "Running" && l.State?.Status == LoadStatus.InTransit)
            .Select(l => l.State!.AssignedUnit)
            .ToHashSet();
        var candidates = s.Trucks.Where(t =>
                t.Entity?.Status == TruckStatus.OnLoad &&
                t.Entity.OpenMaintenanceId == null &&
                t.Telematics?.State == "Driving" &&
                (!inTransitOnly || loadsInTransit.Contains(t.Unit)))
            .Select(t => t.Unit)
            .ToList();
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    /// <summary>
    /// High-severity engine work must stay within about 320 miles. Trucks beyond that distance from every
    /// partner workshop that does engine work get a general workshop recommendation, which needs approval.
    /// Idle trucks are preferred so the case reaches triage right away instead of after a delivery.
    /// </summary>
    private string? PickFarFromPartnerEngineWorkshop(FleetSnapshot s)
    {
        var engineShops = FleetSeed.Shops
            .Where(shop => shop.IsPartner && shop.Capabilities.Contains(IssueCategory.Engine))
            .Select(shop => RoadNetwork.Get(shop.HubKey))
            .ToList();

        double NearestShopMiles(double lat, double lon) =>
            engineShops.Min(h => Geo.DistanceMiles(lat, lon, h.Lat, h.Lon)) * RoadNetwork.RoadFactor;

        var idle = s.Trucks
            .Where(t => t.Entity?.Status == TruckStatus.Available && t.Entity.OpenMaintenanceId == null && t.Telematics != null)
            .Where(t => NearestShopMiles(t.Telematics!.Lat, t.Telematics.Lon) > 340)
            .Select(t => t.Unit)
            .ToList();
        if (idle.Count > 0)
        {
            return idle[rng.Next(idle.Count)];
        }

        // Otherwise a loaded truck whose delivery ends far from a partner engine workshop.
        var loaded = s.Loads
            .Where(l => l.ExecutionStatus == "Running" && l.State?.AssignedUnit != null && l.State.Status == LoadStatus.InTransit)
            .Where(l =>
            {
                var destination = RoadNetwork.Get(l.State!.DestinationHub);
                return NearestShopMiles(destination.Lat, destination.Lon) > 340;
            })
            .Select(l => l.State!.AssignedUnit!)
            .Where(unit => s.Trucks.Any(t => t.Unit == unit && t.Entity?.OpenMaintenanceId == null))
            .ToList();
        return loaded.Count == 0 ? null : loaded[rng.Next(loaded.Count)];
    }

    private string? PickDriving(FleetSnapshot s)
    {
        var candidates = s.Trucks
            .Where(t => t.Telematics?.State == "Driving" && t.Entity?.OpenMaintenanceId == null)
            .Select(t => t.Unit)
            .ToList();
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    private string? PickAny(FleetSnapshot s, params TruckStatus[] statuses)
    {
        var candidates = s.Trucks
            .Where(t => t.Entity != null && statuses.Contains(t.Entity.Status))
            .Select(t => t.Unit)
            .ToList();
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }
}
