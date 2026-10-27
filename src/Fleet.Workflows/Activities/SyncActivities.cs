using Fleet.Core;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Workflows.Activities;

/// <summary>
/// Activities for the scheduled equipment sync. Starting entity workflows is idempotent because the
/// workflow ID is the business key: if truck-41207 already runs, the start is a no-op.
/// </summary>
public class SyncActivities
{
    private readonly ITemporalClient client;
    private readonly DemoSettings settings;

    public SyncActivities(ITemporalClient client, DemoSettings settings)
    {
        this.client = client;
        this.settings = settings;
    }

    [Activity]
    public async Task<EnsureEntitiesResult> EnsureEntityWorkflowsAsync(SalesforceFleetSnapshot snapshot)
    {
        var startedTrucks = new List<string>();
        var startedDrivers = new List<string>();

        foreach (var e in snapshot.Equipment.Where(e => !string.IsNullOrWhiteSpace(e.Unit)))
        {
            var input = new TruckInput
            {
                Unit = e.Unit,
                SalesforceId = e.Id,
                Vin = e.Vin,
                Make = e.Make,
                Model = e.Model,
                Year = e.Year,
                HomeHub = e.HomeHub,
                Odometer = e.Odometer,
                PmDueOdometer = e.PmDueOdometer,
                Settings = settings,
            };
            if (await TryStartAsync(() => client.StartWorkflowAsync(
                    (TruckWorkflow wf) => wf.RunAsync(input),
                    new(id: WorkflowIds.Truck(e.Unit), taskQueue: TaskQueues.Entities))))
            {
                startedTrucks.Add(e.Unit);
            }
        }

        foreach (var d in snapshot.Drivers.Where(d => !string.IsNullOrWhiteSpace(d.DriverId)))
        {
            var input = new DriverInput
            {
                DriverId = d.DriverId,
                Name = d.Name,
                HomeHub = d.HomeHub,
                Endorsements = d.Endorsements,
                Employment = d.HireStatus switch
                {
                    "Active" => DriverEmployment.Active,
                    "Onboarding" => DriverEmployment.Onboarding,
                    _ => DriverEmployment.Inactive,
                },
                SalesforceId = d.Id,
                Phone = d.Phone,
                PairedUnit = d.PairedUnit,
                Settings = settings,
            };
            if (await TryStartAsync(() => client.StartWorkflowAsync(
                    (DriverWorkflow wf) => wf.RunAsync(input),
                    new(id: WorkflowIds.Driver(d.DriverId), taskQueue: TaskQueues.Entities))))
            {
                startedDrivers.Add(d.DriverId);
            }
        }

        return new EnsureEntitiesResult(startedTrucks, startedDrivers);
    }

    [Activity]
    public async Task<List<TruckStatusSnapshot>> GetTruckStatusesAsync(List<string> units)
    {
        var results = await Task.WhenAll(units.Select(async unit =>
        {
            try
            {
                var state = await client.GetWorkflowHandle<TruckWorkflow>(WorkflowIds.Truck(unit)).QueryAsync(wf => wf.GetState());
                return new TruckStatusSnapshot(unit, state.Status, state.CurrentLoadId, state.StatusVersion, state.StatusSince);
            }
            catch (Exception)
            {
                return null;
            }
        }));
        return results.Where(r => r != null).Select(r => r!).ToList();
    }

    private static async Task<bool> TryStartAsync(Func<Task> start)
    {
        try
        {
            await start();
            return true;
        }
        catch (WorkflowAlreadyStartedException)
        {
            return false;
        }
    }
}
