using Fleet.Core;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Workflows.Activities;

/// <summary>
/// Request/response calls between entity workflows. A workflow cannot call an Update on another workflow
/// directly, so an activity does it with the Temporal client. The target entity's validator is the
/// authority: if it rejects (truck in the shop, driver still onboarding), the caller gets a typed,
/// non-retryable failure and moves on to the next candidate.
/// </summary>
public class EntityActivities
{
    private readonly ITemporalClient client;

    public EntityActivities(ITemporalClient client) => this.client = client;

    [Activity]
    public async Task<ReserveTruckResult> ReserveTruckAsync(string unit, ReserveTruckRequest request)
    {
        try
        {
            return await Truck(unit).ExecuteUpdateAsync(wf => wf.ReserveForLoadAsync(request));
        }
        catch (WorkflowUpdateFailedException e)
        {
            throw Rejected("TruckUnavailable", e);
        }
    }

    [Activity]
    public async Task<bool> ReleaseTruckAsync(string unit, ReleaseTruckRequest request) =>
        await Truck(unit).ExecuteUpdateAsync(wf => wf.ReleaseFromLoadAsync(request));

    [Activity]
    public async Task MarkTruckDispatchedAsync(string unit, string loadId) =>
        await Truck(unit).ExecuteUpdateAsync(wf => wf.MarkDispatchedAsync(loadId));

    [Activity]
    public async Task<ReserveDriverResult> ReserveDriverAsync(string driverId, ReserveDriverRequest request)
    {
        try
        {
            return await Driver(driverId).ExecuteUpdateAsync(wf => wf.ReserveForLoadAsync(request));
        }
        catch (WorkflowUpdateFailedException e)
        {
            throw Rejected("DriverUnavailable", e);
        }
    }

    [Activity]
    public async Task<bool> ReleaseDriverAsync(string driverId, string loadId) =>
        await Driver(driverId).ExecuteUpdateAsync(wf => wf.ReleaseFromLoadAsync(loadId));

    [Activity]
    public async Task<MaintenanceHoldResult> PlaceMaintenanceHoldAsync(string unit, MaintenanceHoldRequest request) =>
        await Truck(unit).ExecuteUpdateAsync(wf => wf.PlaceMaintenanceHoldAsync(request));

    [Activity]
    public async Task MarkTruckInShopAsync(string unit, string maintenanceId) =>
        await Truck(unit).ExecuteUpdateAsync(wf => wf.MarkInShopAsync(maintenanceId));

    [Activity]
    public async Task<bool> CompleteTruckMaintenanceAsync(string unit, MaintenanceCompletion completion) =>
        await Truck(unit).ExecuteUpdateAsync(wf => wf.CompleteMaintenanceAsync(completion));

    [Activity]
    public async Task CancelMaintenanceHoldAsync(string unit, string maintenanceId) =>
        await Truck(unit).ExecuteUpdateAsync(wf => wf.CancelMaintenanceHoldAsync(maintenanceId));

    [Activity]
    public async Task RequestTruckStatusRepublishAsync(string unit, string reason) =>
        await Truck(unit).SignalAsync(wf => wf.RepublishStatusAsync(reason));

    private WorkflowHandle<TruckWorkflow> Truck(string unit) =>
        client.GetWorkflowHandle<TruckWorkflow>(WorkflowIds.Truck(unit));

    private WorkflowHandle<DriverWorkflow> Driver(string driverId) =>
        client.GetWorkflowHandle<DriverWorkflow>(WorkflowIds.Driver(driverId));

    private static ApplicationFailureException Rejected(string defaultType, WorkflowUpdateFailedException e)
    {
        var inner = e.InnerException as ApplicationFailureException;
        var message = inner?.Message ?? e.InnerException?.Message ?? e.Message;
        return new ApplicationFailureException(message, errorType: inner?.ErrorType ?? defaultType, nonRetryable: true);
    }
}
