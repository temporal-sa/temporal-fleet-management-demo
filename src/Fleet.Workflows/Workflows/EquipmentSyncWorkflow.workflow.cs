using Fleet.Core;
using Fleet.Workflows.Activities;
using Temporalio.Workflows;

namespace Fleet.Workflows;

/// <summary>
/// Runs on a Temporal Schedule (every minute in the demo). It is the reconciliation backstop for the
/// Salesforce to TMS equipment sync:
///  - Salesforce is the system of record for which trucks and drivers exist; missing entity workflows are started.
///  - The truck entity is the single writer of operational status; if the TMS disagrees, the entity re-publishes.
/// Event-driven updates handle the normal case. This catches the edits that bypass the normal path.
/// </summary>
[Workflow]
public class EquipmentSyncWorkflow
{
    [WorkflowRun]
    public async Task<SyncReport> RunAsync()
    {
        var settings = new DemoSettings();
        var report = new SyncReport { RanAt = Workflow.UtcNow };

        var snapshot = await Workflow.ExecuteActivityAsync(
            (SalesforceActivities a) => a.FetchFleetAsync(), ActivityProfiles.Integration(settings));
        report.EquipmentRecords = snapshot.Equipment.Count;
        report.DriverRecords = snapshot.Drivers.Count;

        var ensured = await Workflow.ExecuteActivityAsync(
            (SyncActivities a) => a.EnsureEntityWorkflowsAsync(snapshot), ActivityProfiles.Local());
        report.StartedEntities.AddRange(ensured.StartedTrucks.Select(WorkflowIds.Truck));
        report.StartedEntities.AddRange(ensured.StartedDrivers.Select(WorkflowIds.Driver));

        var tms = await Workflow.ExecuteActivityAsync(
            (TmsActivities a) => a.FetchEquipmentStatusesAsync(), ActivityProfiles.Integration(settings));
        var tmsByUnit = new SortedDictionary<string, TmsEquipmentStatus>();
        foreach (var m in tms)
        {
            tmsByUnit[m.Unit] = m;
        }

        var units = snapshot.Equipment.Select(e => e.Unit).OrderBy(u => u, StringComparer.Ordinal).ToList();
        var entities = await Workflow.ExecuteActivityAsync(
            (SyncActivities a) => a.GetTruckStatusesAsync(units), ActivityProfiles.Local());

        foreach (var truck in entities.OrderBy(t => t.Unit, StringComparer.Ordinal))
        {
            var expected = StatusMapping.ToTms(truck.Status);
            tmsByUnit.TryGetValue(truck.Unit, out var current);
            if (current != null && current.Status == expected)
            {
                continue;
            }

            // Skip trucks whose status changed in the last few seconds; their own write may still be in flight.
            if (Workflow.UtcNow - truck.StatusSince < TimeSpan.FromSeconds(15))
            {
                continue;
            }

            var tmsValue = current == null ? "missing" : $"{current.Status} (v{current.Version}, by {current.Source})";
            var reason = $"TMS shows {tmsValue}; the truck entity is {truck.Status} (v{truck.StatusVersion}).";
            var unit = truck.Unit;
            await Workflow.ExecuteActivityAsync(
                (EntityActivities a) => a.RequestTruckStatusRepublishAsync(unit, reason), ActivityProfiles.Entity());
            report.Conflicts.Add(new SyncConflict(
                truck.Unit, truck.Status.ToString(), current?.Status ?? "missing", "Truck entity re-published its status (single writer)."));
        }

        return report;
    }
}
