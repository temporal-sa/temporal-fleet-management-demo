using Fleet.Core;
using Fleet.Integrations;
using Temporalio.Activities;

namespace Fleet.Workflows.Activities;

/// <summary>TMS integration (fleet-integrations task queue).</summary>
public class TmsActivities
{
    private readonly TmsClient tms;

    public TmsActivities(TmsClient tms) => this.tms = tms;

    /// <summary>
    /// Writes carry the truck entity's status version. The TMS ignores anything older than what it has,
    /// so out-of-order delivery after an outage cannot overwrite newer state.
    /// </summary>
    [Activity]
    public Task<TmsWriteResult> UpdateEquipmentStatusAsync(TmsEquipmentUpdate update)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => tms.UpdateEquipmentStatusAsync(update, ct));
    }

    /// <summary>Idempotent by IdempotencyKey, so a retried milestone is recorded once.</summary>
    [Activity]
    public Task RecordLoadMilestoneAsync(LoadMilestone milestone)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => tms.RecordLoadMilestoneAsync(milestone, ct));
    }

    [Activity]
    public Task<List<TmsEquipmentStatus>> FetchEquipmentStatusesAsync()
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => tms.GetEquipmentAsync(ct));
    }

    [Activity]
    public Task UpsertDriverProfileAsync(TmsDriverProfile profile)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => tms.UpsertDriverAsync(profile, ct));
    }
}
