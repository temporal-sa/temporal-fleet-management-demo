using Fleet.Core;
using Fleet.Workflows.Activities;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Fleet.Workflows;

/// <summary>
/// Entity workflow: one long-lived workflow per tractor, ID "truck-{unit}".
///
/// It is the single writer of the truck's operational status. Loads reserve it, maintenance holds it,
/// telematics anomalies arrive as signals, and every status change is pushed to the TMS with a version
/// number. Because all changes for one truck pass through one place, there is no race between
/// "assign a load" and "truck went to the shop".
/// </summary>
[Workflow]
public class TruckWorkflow
{
    private const int MaxEventsPerRun = 400;

    private readonly TruckState state;
    private readonly DemoSettings settings;
    private readonly Queue<Anomaly> pendingAnomalies = new();
    private readonly Queue<string> maintenanceToNotify = new();
    private bool tmsDirty;
    private string tmsReason = "";
    private Task? tmsPush;
    private int handledThisRun;

    [WorkflowInit]
    public TruckWorkflow(TruckInput input)
    {
        settings = input.Settings;
        if (input.CarryOver != null)
        {
            state = input.CarryOver;
            state.RunNumber++;
        }
        else
        {
            state = new TruckState
            {
                Unit = input.Unit,
                SalesforceId = input.SalesforceId,
                Vin = input.Vin,
                Make = input.Make,
                Model = input.Model,
                Year = input.Year,
                HomeHub = input.HomeHub,
                Odometer = input.Odometer,
                PmDueOdometer = input.PmDueOdometer,
            };
        }
    }

    [WorkflowQuery]
    public TruckState GetState() => state;

    [WorkflowRun]
    public async Task RunAsync(TruckInput input)
    {
        if (state.StatusSince == default)
        {
            state.StatusSince = Workflow.UtcNow;
        }

        FleetVisibility.Upsert(settings, 
            FleetSearchAttributes.Entity.ValueSet("Truck"),
            FleetSearchAttributes.Unit.ValueSet(state.Unit),
            FleetSearchAttributes.Status.ValueSet(state.Status.ToString()),
            FleetSearchAttributes.NeedsAttention.ValueSet(NeedsAttention));

        if (input.CarryOver == null)
        {
            AddEvent("info", $"Truck entity started from Salesforce record {state.SalesforceId ?? "(none)"}.");
            tmsDirty = true;
            tmsReason = "initial sync";
        }
        else
        {
            AddEvent("info", $"Continued as new (run {state.RunNumber}) to keep history small. State carried over.");
        }

        while (true)
        {
            await Workflow.WaitConditionAsync(() =>
                pendingAnomalies.Count > 0 ||
                maintenanceToNotify.Count > 0 ||
                (tmsDirty && tmsPush == null) ||
                ShouldContinueAsNew);

            while (pendingAnomalies.Count > 0)
            {
                await HandleAnomalyAsync(pendingAnomalies.Dequeue());
            }

            while (maintenanceToNotify.Count > 0)
            {
                await NotifyMaintenanceAsync(maintenanceToNotify.Dequeue());
            }

            // TMS writes run in their own lane: a TMS outage must not stop this truck from
            // opening a repair case or accepting a hold. The push retries until the TMS is back.
            if (tmsDirty && tmsPush == null)
            {
                tmsDirty = false;
                tmsPush = PushStatusToTmsAsync();
                if (tmsPush.IsCompleted)
                {
                    tmsPush = null;
                }
            }

            if (ShouldContinueAsNew)
            {
                await Workflow.WaitConditionAsync(() => tmsPush == null && Workflow.AllHandlersFinished);
                if (pendingAnomalies.Count > 0 || maintenanceToNotify.Count > 0 || tmsDirty)
                {
                    continue;
                }

                var next = new TruckInput { Unit = state.Unit, Settings = settings, CarryOver = state };
                throw Workflow.CreateContinueAsNewException((TruckWorkflow wf) => wf.RunAsync(next));
            }
        }
    }

    private bool ShouldContinueAsNew => handledThisRun >= MaxEventsPerRun || Workflow.ContinueAsNewSuggested;

    private bool NeedsAttention => state.Status is TruckStatus.Breakdown or TruckStatus.OutOfService;

    // ---------------------------------------------------------------- signals

    [WorkflowSignal(Signals.AnomalyDetected)]
    public async Task AnomalyDetectedAsync(Anomaly anomaly)
    {
        handledThisRun++;
        pendingAnomalies.Enqueue(anomaly);
    }

    [WorkflowSignal(Signals.RepublishStatus)]
    public async Task RepublishStatusAsync(string reason)
    {
        handledThisRun++;
        tmsDirty = true;
        tmsReason = "reconcile";
        AddEvent("integration", $"Reconciliation asked for a re-publish: {reason}");
    }

    [WorkflowSignal(Signals.SyncMasterData)]
    public async Task SyncMasterDataAsync(EquipmentMasterData data)
    {
        handledThisRun++;
        if (data.HomeHub != state.HomeHub)
        {
            AddEvent("integration", $"Home terminal changed in Salesforce: {state.HomeHub} to {data.HomeHub}.");
        }

        state.SalesforceId = data.SalesforceId ?? state.SalesforceId;
        state.HomeHub = data.HomeHub;
        state.PmDueOdometer = data.PmDueOdometer;
    }

    // ---------------------------------------------------------------- updates

    [WorkflowUpdateValidator(nameof(ReserveForLoadAsync))]
    public void ValidateReserveForLoad(ReserveTruckRequest request)
    {
        if (state.CurrentLoadId == request.LoadId)
        {
            return; // Idempotent retry of the same reservation.
        }

        if (state.OpenMaintenanceId != null)
        {
            throw new ApplicationFailureException(
                $"Unit {state.Unit} has open maintenance case {state.OpenMaintenanceId} ({Describe(state.Status)}). " +
                "It can't take new loads until the repair closes.",
                errorType: "TruckUnavailable");
        }

        if (state.Status != TruckStatus.Available)
        {
            var load = state.CurrentLoadId != null ? $" on load {state.CurrentLoadId}" : "";
            throw new ApplicationFailureException(
                $"Unit {state.Unit} is {Describe(state.Status)}{load}.", errorType: "TruckUnavailable");
        }
    }

    [WorkflowUpdate]
    public async Task<ReserveTruckResult> ReserveForLoadAsync(ReserveTruckRequest request)
    {
        handledThisRun++;
        if (state.CurrentLoadId != request.LoadId)
        {
            state.CurrentLoadId = request.LoadId;
            state.CurrentDriverId = request.DriverId;
            SetStatus(TruckStatus.Reserved, $"reserved for load {request.LoadId}");
        }

        return new ReserveTruckResult(state.Unit, request.LoadId, state.StatusVersion);
    }

    [WorkflowUpdate]
    public async Task MarkDispatchedAsync(string loadId)
    {
        handledThisRun++;
        if (state.CurrentLoadId == loadId && state.Status == TruckStatus.Reserved)
        {
            SetStatus(TruckStatus.OnLoad, $"dispatched on load {loadId}");
        }
    }

    [WorkflowUpdate]
    public async Task<bool> ReleaseFromLoadAsync(ReleaseTruckRequest request)
    {
        handledThisRun++;
        if (state.CurrentLoadId != request.LoadId)
        {
            return false; // Already released. Safe to retry.
        }

        state.CurrentLoadId = null;
        state.CurrentDriverId = null;
        if (request.Delivered)
        {
            state.LoadsCompleted++;
        }

        var verb = request.Delivered ? "delivered" : "released";
        if (state.Status is TruckStatus.Reserved or TruckStatus.OnLoad)
        {
            if (state.OpenMaintenanceId != null && state.MaintenanceDeferred)
            {
                state.MaintenanceDeferred = false;
                SetStatus(TruckStatus.ShopBound, $"load {request.LoadId} {verb}; repair {state.OpenMaintenanceId} can start");
                maintenanceToNotify.Enqueue(state.OpenMaintenanceId);
            }
            else
            {
                SetStatus(TruckStatus.Available, $"load {request.LoadId} {verb}");
            }
        }
        else
        {
            AddEvent("info", $"Released from load {request.LoadId} while {Describe(state.Status)}.");
        }

        return true;
    }

    [WorkflowUpdate]
    public async Task<MaintenanceHoldResult> PlaceMaintenanceHoldAsync(MaintenanceHoldRequest request)
    {
        handledThisRun++;
        state.OpenMaintenanceId ??= request.MaintenanceId;
        var activeLoad = state.CurrentLoadId;

        if (request.Roadside)
        {
            state.MaintenanceDeferred = false;
            SetStatus(TruckStatus.Breakdown, $"roadside event: {request.Reason}");
            return new MaintenanceHoldResult(activeLoad, false);
        }

        if (activeLoad != null && (state.Status is TruckStatus.Reserved or TruckStatus.OnLoad))
        {
            state.MaintenanceDeferred = true;
            AddEvent("maintenance", $"Repair deferred until load {activeLoad} delivers. No new loads will be accepted.");
            UpsertAttributes();
            return new MaintenanceHoldResult(activeLoad, true);
        }

        state.MaintenanceDeferred = false;
        SetStatus(TruckStatus.ShopBound, $"held for maintenance: {request.Reason}");
        return new MaintenanceHoldResult(null, false);
    }

    [WorkflowUpdate]
    public async Task MarkInShopAsync(string maintenanceId)
    {
        handledThisRun++;
        if (state.OpenMaintenanceId == maintenanceId)
        {
            SetStatus(TruckStatus.InShop, $"arrived at the shop for {maintenanceId}");
        }
    }

    [WorkflowUpdate]
    public async Task<bool> CompleteMaintenanceAsync(MaintenanceCompletion completion)
    {
        handledThisRun++;
        if (state.OpenMaintenanceId != completion.MaintenanceId)
        {
            return false;
        }

        state.OpenMaintenanceId = null;
        state.MaintenanceDeferred = false;
        state.MaintenanceSpend += completion.Cost;
        if (state.Status is TruckStatus.Reserved or TruckStatus.OnLoad)
        {
            AddEvent("maintenance", $"Repair {completion.MaintenanceId} closed (${completion.Cost:N0}).");
        }
        else
        {
            SetStatus(TruckStatus.Available, $"repair {completion.MaintenanceId} closed (${completion.Cost:N0}); back in service");
        }

        return true;
    }

    [WorkflowUpdate]
    public async Task CancelMaintenanceHoldAsync(string maintenanceId)
    {
        handledThisRun++;
        if (state.OpenMaintenanceId != maintenanceId)
        {
            return;
        }

        state.OpenMaintenanceId = null;
        state.MaintenanceDeferred = false;
        if (state.Status is TruckStatus.ShopBound or TruckStatus.InShop or TruckStatus.Breakdown)
        {
            SetStatus(TruckStatus.Available, $"maintenance {maintenanceId} cancelled; hold released");
        }
        else
        {
            AddEvent("maintenance", $"Maintenance {maintenanceId} cancelled.");
        }
    }

    // ---------------------------------------------------------------- internals

    private async Task HandleAnomalyAsync(Anomaly anomaly)
    {
        AddEvent("signal", $"Telematics anomaly {anomaly.Code}: {anomaly.Description}");

        if (state.OpenMaintenanceId != null)
        {
            // One truck, one open case. Related anomalies merge instead of opening duplicates.
            state.AnomaliesMergedIntoOpenCase++;
            try
            {
                await Workflow.GetExternalWorkflowHandle<MaintenanceWorkflow>(state.OpenMaintenanceId)
                    .SignalAsync(wf => wf.AdditionalAnomalyAsync(anomaly));
                AddEvent("dedupe", $"Merged into open case {state.OpenMaintenanceId} instead of opening a duplicate.");
            }
            catch (Exception e)
            {
                AddEvent("warn", $"Could not reach {state.OpenMaintenanceId}: {e.Message}");
            }

            return;
        }

        state.MaintenanceSequence++;
        var maintenanceId = WorkflowIds.Maintenance(state.Unit, state.MaintenanceSequence);
        var trigger = anomaly.IsBreakdown
            ? MaintenanceTrigger.Breakdown
            : anomaly.Category == IssueCategory.PreventiveService ? MaintenanceTrigger.Preventive : MaintenanceTrigger.Predictive;
        state.OpenMaintenanceId = maintenanceId;

        var childInput = new MaintenanceInput
        {
            MaintenanceId = maintenanceId,
            Unit = state.Unit,
            Trigger = trigger,
            Anomaly = anomaly,
            Settings = settings,
        };

        try
        {
            await Workflow.StartChildWorkflowAsync(
                (MaintenanceWorkflow wf) => wf.RunAsync(childInput),
                new()
                {
                    Id = maintenanceId,
                    TaskQueue = TaskQueues.Maintenance,
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                });
            AddEvent("maintenance", $"Opened maintenance case {maintenanceId} ({anomaly.Category}, {anomaly.Severity}).");
        }
        catch (Exception e)
        {
            AddEvent("warn", $"Maintenance case {maintenanceId} was not started: {e.Message}");
        }

        UpsertAttributes();
    }

    private async Task NotifyMaintenanceAsync(string maintenanceId)
    {
        try
        {
            await Workflow.GetExternalWorkflowHandle<MaintenanceWorkflow>(maintenanceId)
                .SignalAsync(wf => wf.TruckReleasedAsync(state.Unit));
        }
        catch (Exception e)
        {
            AddEvent("warn", $"Could not notify {maintenanceId}: {e.Message}");
        }
    }

    private async Task PushStatusToTmsAsync()
    {
        try
        {
            var update = new TmsEquipmentUpdate(
                state.Unit, StatusMapping.ToTms(state.Status), state.CurrentLoadId, state.StatusVersion, "temporal:truck-entity");
            var reason = tmsReason;
            var result = await Workflow.ExecuteActivityAsync(
                (TmsActivities a) => a.UpdateEquipmentStatusAsync(update),
                ActivityProfiles.Integration(settings));

            if (!result.Applied)
            {
                AddEvent("integration", $"The TMS already has version {result.CurrentVersion}; older write skipped.");
            }
            else if (reason == "reconcile")
            {
                AddEvent("integration", $"Re-published {update.Status} (v{update.Version}) to the TMS. Conflict resolved.");
            }
            else
            {
                AddEvent("integration", $"TMS updated: {update.Status} (v{update.Version}).");
            }
        }
        catch (ActivityFailureException e)
        {
            AddEvent("warn", $"The TMS rejected the status update: {e.InnerException?.Message ?? e.Message}");
        }
        finally
        {
            tmsPush = null;
        }
    }

    private void SetStatus(TruckStatus status, string reason)
    {
        if (state.Status == status)
        {
            AddEvent("status", Capitalize(reason));
            return;
        }

        state.Status = status;
        state.StatusSince = Workflow.UtcNow;
        state.StatusVersion++;
        AddEvent("status", $"{Describe(status)}: {reason}");
        tmsDirty = true;
        tmsReason = "status change";
        UpsertAttributes();
    }

    private void UpsertAttributes() =>
        FleetVisibility.Upsert(settings, 
            FleetSearchAttributes.Status.ValueSet(state.Status.ToString()),
            FleetSearchAttributes.NeedsAttention.ValueSet(NeedsAttention));

    private void AddEvent(string kind, string text) =>
        state.RecentEvents.AddCapped(new EntityEvent(Workflow.UtcNow, kind, text), 30);

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Describe(TruckStatus status) => status switch
    {
        TruckStatus.Available => "Available",
        TruckStatus.Reserved => "Reserved",
        TruckStatus.OnLoad => "On a load",
        TruckStatus.ShopBound => "Heading to a shop",
        TruckStatus.InShop => "In the shop",
        TruckStatus.Breakdown => "Broken down",
        TruckStatus.OutOfService => "Out of service",
        _ => status.ToString(),
    };
}
