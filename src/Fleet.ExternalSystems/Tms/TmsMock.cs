using Fleet.Core;
using Fleet.ExternalSystems.Infrastructure;

namespace Fleet.ExternalSystems.Tms;

public record MilestoneRecord(string Milestone, string? Unit, string? DriverId, DateTime RecordedAt);

/// <summary>
/// Mock of a transportation management system (TMS). The endpoints are illustrative and do not model any
/// particular product. Two properties matter for the demo: an outage switch and versioned equipment writes,
/// so a stale update can never overwrite a newer one.
/// </summary>
public class TmsStore
{
    private readonly object gate = new();
    private readonly SortedDictionary<string, TmsEquipmentStatus> equipment = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MilestoneRecord>> loads = new();
    private readonly HashSet<string> milestoneKeys = new();
    private readonly SortedDictionary<string, TmsDriverProfile> drivers = new(StringComparer.Ordinal);

    public TmsStore()
    {
        foreach (var t in FleetSeed.Trucks)
        {
            equipment[t.Unit] = new TmsEquipmentStatus(t.Unit, "AVAILABLE", null, 0, "seed", DateTime.UtcNow);
        }

        foreach (var d in FleetSeed.Drivers.Where(d => d.Employment == DriverEmployment.Active))
        {
            drivers[d.DriverId] = new TmsDriverProfile(d.DriverId, d.Name, "ACTIVE", d.HomeHub);
        }
    }

    public CallLog Calls { get; } = new();

    public TmsWriteResult UpdateEquipment(TmsEquipmentUpdate update)
    {
        lock (gate)
        {
            if (equipment.TryGetValue(update.Unit, out var current) && update.Version < current.Version)
            {
                return new TmsWriteResult(false, current.Version, $"Ignored stale write v{update.Version}; have v{current.Version}");
            }

            equipment[update.Unit] = new TmsEquipmentStatus(update.Unit, update.Status, update.LoadId, update.Version, update.Source, DateTime.UtcNow);
            return new TmsWriteResult(true, update.Version, "applied");
        }
    }

    public TmsEquipmentStatus? InjectConflict(string unit)
    {
        lock (gate)
        {
            if (!equipment.TryGetValue(unit, out var current))
            {
                return null;
            }

            // Someone edits the record directly in the TMS screen. Same version, different status.
            var rogue = current.Status == "AVAILABLE" ? "OUT_OF_SERVICE" : "AVAILABLE";
            var edited = current with { Status = rogue, Source = "manual edit in the TMS", UpdatedAt = DateTime.UtcNow };
            equipment[unit] = edited;
            return edited;
        }
    }

    public List<TmsEquipmentStatus> Equipment()
    {
        lock (gate)
        {
            return equipment.Values.ToList();
        }
    }

    public bool RecordMilestone(LoadMilestone m)
    {
        lock (gate)
        {
            if (!milestoneKeys.Add(m.IdempotencyKey))
            {
                return false;
            }

            if (!loads.TryGetValue(m.LoadId, out var list))
            {
                list = new List<MilestoneRecord>();
                loads[m.LoadId] = list;
            }

            list.Add(new MilestoneRecord(m.Milestone, m.Unit, m.DriverId, DateTime.UtcNow));
            return true;
        }
    }

    public object Loads()
    {
        lock (gate)
        {
            return loads
                .OrderByDescending(kv => kv.Value.LastOrDefault()?.RecordedAt ?? DateTime.MinValue)
                .Take(40)
                .Select(kv => new { loadId = kv.Key, milestones = kv.Value.ToList() })
                .ToList();
        }
    }

    public void UpsertDriver(TmsDriverProfile profile)
    {
        lock (gate)
        {
            drivers[profile.DriverId] = profile;
        }
    }

    public List<TmsDriverProfile> Drivers()
    {
        lock (gate)
        {
            return drivers.Values.ToList();
        }
    }
}

public static class TmsEndpoints
{
    public static void MapTms(this WebApplication app)
    {
        var store = app.Services.GetRequiredService<TmsStore>();
        var chaos = app.Services.GetRequiredService<ChaosState>();
        var log = app.Services.GetRequiredService<EventLog>();

        IResult Outage(string method, string path)
        {
            store.Calls.Add(method, path, 503, "unavailable");
            return Results.Json(new { errorCode = "SERVICE_UNAVAILABLE", message = "TMS is unavailable" }, statusCode: 503);
        }

        app.MapPut("/tms/api/equipment/{unit}/status", (string unit, TmsEquipmentUpdate update) =>
        {
            var path = $"equipment/{unit}/status";
            if (chaos.TmsOutage)
            {
                return Outage("PUT", path);
            }

            var result = store.UpdateEquipment(update with { Unit = unit });
            store.Calls.Add("PUT", path, 200, result.Applied ? $"{update.Status} v{update.Version}" : result.Note);
            return Results.Json(result);
        });

        app.MapGet("/tms/api/equipment", () =>
        {
            if (chaos.TmsOutage)
            {
                return Outage("GET", "equipment");
            }

            store.Calls.Add("GET", "equipment", 200);
            return Results.Json(store.Equipment());
        });

        app.MapPost("/tms/api/loads/{loadId}/milestones", (string loadId, LoadMilestone milestone) =>
        {
            var path = $"loads/{loadId}/milestones";
            if (chaos.TmsOutage)
            {
                return Outage("POST", path);
            }

            var recorded = store.RecordMilestone(milestone with { LoadId = loadId });
            store.Calls.Add("POST", path, 200, recorded ? milestone.Milestone : $"{milestone.Milestone} (duplicate ignored)");
            return Results.Json(new { recorded });
        });

        app.MapGet("/tms/api/loads", () => Results.Json(store.Loads()));

        app.MapPut("/tms/api/drivers/{driverId}", (string driverId, TmsDriverProfile profile) =>
        {
            var path = $"drivers/{driverId}";
            if (chaos.TmsOutage)
            {
                return Outage("PUT", path);
            }

            store.UpsertDriver(profile with { DriverId = driverId });
            store.Calls.Add("PUT", path, 200, profile.Status);
            log.Add("TMS", "info", $"Driver profile {driverId} ({profile.Name}) set to {profile.Status}.");
            return Results.Ok();
        });

        app.MapGet("/tms/api/drivers", () => Results.Json(store.Drivers()));

        app.MapGet("/tms/admin/calls", () => Results.Json(store.Calls.Recent()));

        // Demo-only read that ignores the outage switch, so the UI can still show what the TMS holds.
        app.MapGet("/tms/admin/equipment", () => Results.Json(store.Equipment()));

        app.MapPost("/tms/admin/inject-conflict/{unit}", (string unit) =>
        {
            var edited = store.InjectConflict(unit);
            if (edited == null)
            {
                return Results.NotFound();
            }

            log.Add("TMS", "warn", $"Unit {unit} edited by hand in the TMS: now {edited.Status} (v{edited.Version}). Out of sync with operations.");
            return Results.Json(edited);
        });
    }
}
