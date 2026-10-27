using Fleet.Core;
using Fleet.Workflows.Activities;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Fleet.Workflows;

/// <summary>
/// The breakdown and maintenance process, end to end, as one durable workflow per case.
///
///  1. Hold the truck through its entity. Roadside events take it out of service and re-power its load.
///  2. Open (upsert) the Salesforce work order. Retries can never create a duplicate.
///  3. Non-urgent work waits for the current load to deliver, then continues on its own.
///  4. Rank workshops (partners first when it makes sense) and get a recommendation from Claude or the policy engine.
///  5. Expensive general workshop work waits for a fleet manager, with a timeout and a policy fallback.
///  6. Request service. A vendor that does not answer in 2 hours (simulated) is skipped for the next one.
///  7. Route or tow the truck to the shop, wait for arrival and completion, then close everything out.
/// Cancelling the case runs the saga compensations in reverse order.
/// </summary>
[Workflow]
public class MaintenanceWorkflow
{
    private readonly MaintenanceState state;
    private readonly DemoSettings settings;
    private readonly SortedDictionary<string, VendorResponse> vendorResponses = new();
    private ApprovalDecision? decision;
    private RepairCompletion? completion;
    private bool arrivedAtShop;
    private bool truckReleased;
    private bool cancelRequested;
    private bool escalatedToRoadside;
    private string? acceptedRequestId;

    [WorkflowInit]
    public MaintenanceWorkflow(MaintenanceInput input)
    {
        settings = input.Settings;
        var a = input.Anomaly;
        state = new MaintenanceState
        {
            MaintenanceId = input.MaintenanceId,
            Unit = input.Unit,
            Trigger = input.Trigger,
            Category = a.Category,
            Severity = a.Severity,
            Description = a.Description,
            AnomalyCode = a.Code,
            Roadside = input.Trigger == MaintenanceTrigger.Breakdown || a.Severity == Severity.Critical,
        };
    }

    [WorkflowQuery]
    public MaintenanceState GetState() => state;

    [WorkflowRun]
    public async Task<MaintenanceResult> RunAsync(MaintenanceInput input)
    {
        state.StartedAt = Workflow.UtcNow;
        FleetVisibility.Upsert(settings, 
            FleetSearchAttributes.Entity.ValueSet("Maintenance"),
            FleetSearchAttributes.Unit.ValueSet(state.Unit),
            FleetSearchAttributes.Status.ValueSet(state.Phase.ToString()),
            FleetSearchAttributes.NeedsAttention.ValueSet(false));
        Timeline("signal", $"Opened from the telematics stream: {state.AnomalyCode}, {state.Description}");

        var compensations = new List<Func<Task>>();
        try
        {
            await HoldTruckAsync(compensations);

            compensations.Add(() => UpsertWorkOrderAsync("Canceled"));
            var workOrder = await UpsertWorkOrderAsync("New");
            state.WorkOrderId = workOrder.Id;
            state.WorkOrderNumber = workOrder.Number;
            Timeline("integration", $"Salesforce work order {workOrder.Number ?? workOrder.Id} {(workOrder.Created ? "created" : "already existed; updated")}.");

            if (state.DeferredForLoadId != null)
            {
                SetPhase(MaintenancePhase.WaitingForLoadCompletion);
                await Workflow.WaitConditionAsync(() => truckReleased || cancelRequested || escalatedToRoadside);
                ThrowIfCancelled();
                if (escalatedToRoadside && !truckReleased)
                {
                    Timeline("warn", "Severity escalated while the truck was still on its load. Switching to a roadside response.");
                    await HoldTruckAsync(null);
                }
            }

            var chosen = await ChooseShopAsync();
            ThrowIfCancelled();

            var selected = await SourceShopAsync(chosen, compensations);
            state.SelectedShop = selected;
            state.EstimatedCost = selected.EstimatedCost;
            state.GeneralWorkshopBaseline = selected.GeneralWorkshopBaseline;
            await UpsertWorkOrderAsync("Scheduled");

            SetPhase(MaintenancePhase.EnRouteToShop);
            var hub = RoadNetwork.Get(selected.HubKey);
            var dispatch = new ShopDispatch(
                state.Unit, state.MaintenanceId, Workflow.Info.WorkflowId, selected.ShopId, selected.Name, hub.Lat, hub.Lon, state.Roadside);
            await Workflow.ExecuteActivityAsync((MaintenanceActivities a) => a.DispatchToShopAsync(dispatch), ActivityProfiles.Local());
            Timeline("info", state.Roadside
                ? $"Tow dispatched. The truck goes to {selected.Name}."
                : $"Route sent to the driver: {selected.Name}, {selected.DistanceMiles:0} mi.");

            await Workflow.WaitConditionAsync(() => arrivedAtShop || cancelRequested);
            ThrowIfCancelled();

            var unit = state.Unit;
            var maintenanceId = state.MaintenanceId;
            await Workflow.ExecuteActivityAsync((EntityActivities a) => a.MarkTruckInShopAsync(unit, maintenanceId), ActivityProfiles.Entity());

            SetPhase(MaintenancePhase.InRepair);
            await UpsertWorkOrderAsync("In Progress");
            var requestId = acceptedRequestId!;
            await Workflow.ExecuteActivityAsync((MaintenanceActivities a) => a.NotifyShopArrivalAsync(requestId), ActivityProfiles.Local());

            await Workflow.WaitConditionAsync(() => completion != null || cancelRequested);
            ThrowIfCancelled();

            var cost = completion!.ActualCost;
            state.ActualCost = cost;
            state.Savings = selected.IsPartner ? Math.Max(0, state.GeneralWorkshopBaseline - cost) : 0;
            await UpsertWorkOrderAsync("Completed");

            var done = new MaintenanceCompletion(maintenanceId, cost);
            await Workflow.ExecuteActivityAsync((EntityActivities a) => a.CompleteTruckMaintenanceAsync(unit, done), ActivityProfiles.Entity());

            state.CompletedAt = Workflow.UtcNow;
            SetPhase(MaintenancePhase.Completed);
            Timeline("success", selected.IsPartner
                ? $"Repaired at a partner workshop for ${cost:N0}. A typical general workshop invoice would have been ${state.GeneralWorkshopBaseline:N0}: ${state.Savings:N0} saved."
                : $"Repaired by {selected.Name} for ${cost:N0}.");

            await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
            return Result("Completed");
        }
        catch (CaseCancelledException)
        {
            Timeline("warn", $"Case cancelled ({state.CancelReason}). Running compensations in reverse order.");
            compensations.Reverse();
            foreach (var compensate in compensations)
            {
                try
                {
                    await compensate();
                }
                catch (Exception e)
                {
                    Timeline("error", $"A compensation step failed: {e.Message}");
                }
            }

            SetPhase(MaintenancePhase.Cancelled);
            SetAttention(false);
            Timeline("info", "Truck hold released, vendor requests withdrawn, work order closed as Canceled.");
            await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
            return Result("Cancelled");
        }
    }

    // ---------------------------------------------------------------- signals

    [WorkflowSignal(Signals.VendorResponded)]
    public async Task VendorRespondedAsync(VendorResponse response)
    {
        if (vendorResponses.ContainsKey(response.RequestId))
        {
            state.DuplicateEventsIgnored++;
            Timeline("dedupe", "Duplicate vendor response ignored.");
            return;
        }

        vendorResponses[response.RequestId] = response;
    }

    [WorkflowSignal(Signals.ArrivedAtShop)]
    public async Task ArrivedAtShopAsync(GeofenceEvent e)
    {
        if (arrivedAtShop)
        {
            state.DuplicateEventsIgnored++;
            return;
        }

        arrivedAtShop = true;
        Timeline("signal", $"Geofence: truck arrived at {e.StopName}.");
    }

    [WorkflowSignal(Signals.RepairCompleted)]
    public async Task RepairCompletedAsync(RepairCompletion repair)
    {
        if (completion != null)
        {
            state.DuplicateEventsIgnored++;
            Timeline("dedupe", $"Duplicate completion event {repair.EventId} ignored. The work order closes once.");
            return;
        }

        completion = repair;
        Timeline("signal", state.Phase == MaintenancePhase.InRepair
            ? $"Shop reports the repair complete (${repair.ActualCost:N0})."
            : "Completion arrived before arrival was confirmed. It is held until the workflow reaches that step.");
    }

    [WorkflowSignal(Signals.AdditionalAnomaly)]
    public async Task AdditionalAnomalyAsync(Anomaly anomaly)
    {
        state.MergedAnomalies.Add($"{anomaly.Code}: {anomaly.Description}");
        Timeline("dedupe", $"Related anomaly {anomaly.Code} merged into this case.");
        if (anomaly.Severity > state.Severity)
        {
            state.Severity = anomaly.Severity;
            Timeline("warn", $"Severity raised to {anomaly.Severity}.");
        }

        var early = state.Phase is MaintenancePhase.Opened or MaintenancePhase.WaitingForLoadCompletion;
        if ((anomaly.IsBreakdown || anomaly.Severity == Severity.Critical) && !state.Roadside && early)
        {
            state.Roadside = true;
            escalatedToRoadside = true;
        }
    }

    [WorkflowSignal(Signals.TruckReleased)]
    public async Task TruckReleasedAsync(string unit)
    {
        truckReleased = true;
        Timeline("signal", $"Unit {unit} delivered its load. Moving on to shop selection.");
    }

    // ---------------------------------------------------------------- updates (human in the loop)

    [WorkflowUpdateValidator(nameof(DecideAsync))]
    public void ValidateDecide(ApprovalDecision d)
    {
        if (state.Phase != MaintenancePhase.AwaitingApproval)
        {
            throw new ApplicationFailureException($"Case is {state.Phase}, not waiting for a decision.", errorType: "NotAwaitingApproval");
        }

        if (decision != null)
        {
            throw new ApplicationFailureException("A decision was already recorded.", errorType: "AlreadyDecided");
        }

        if (d.AlternateShopId != null && state.Options.All(o => o.ShopId != d.AlternateShopId))
        {
            throw new ApplicationFailureException($"Shop {d.AlternateShopId} is not one of the options.", errorType: "UnknownShop");
        }

        if (string.IsNullOrWhiteSpace(d.DecidedBy))
        {
            throw new ApplicationFailureException("DecidedBy is required.", errorType: "MissingApprover");
        }
    }

    [WorkflowUpdate]
    public async Task<string> DecideAsync(ApprovalDecision d)
    {
        decision = d;
        var note = string.IsNullOrWhiteSpace(d.Note) ? "" : $": {d.Note}";
        Timeline("decision", $"{d.DecidedBy} {(d.Approve ? "approved" : "rejected")} the recommendation{note}");
        return "Recorded";
    }

    [WorkflowUpdateValidator(nameof(CancelCaseAsync))]
    public void ValidateCancel(string reason)
    {
        if (state.Phase is MaintenancePhase.InRepair or MaintenancePhase.Completed or MaintenancePhase.Cancelled)
        {
            throw new ApplicationFailureException($"A case that is {state.Phase} can't be cancelled.", errorType: "CannotCancel");
        }
    }

    [WorkflowUpdate]
    public async Task<string> CancelCaseAsync(string reason)
    {
        cancelRequested = true;
        state.CancelReason = string.IsNullOrWhiteSpace(reason) ? "cancelled by user" : reason;
        return "Cancelling";
    }

    // ---------------------------------------------------------------- steps

    private async Task HoldTruckAsync(List<Func<Task>>? compensations)
    {
        var unit = state.Unit;
        var maintenanceId = state.MaintenanceId;
        compensations?.Add(() => Workflow.ExecuteActivityAsync(
            (EntityActivities a) => a.CancelMaintenanceHoldAsync(unit, maintenanceId), ActivityProfiles.Entity()));

        var hold = new MaintenanceHoldRequest(maintenanceId, state.Severity, state.Roadside, state.Description);
        var result = await Workflow.ExecuteActivityAsync(
            (EntityActivities a) => a.PlaceMaintenanceHoldAsync(unit, hold), ActivityProfiles.Entity());

        if (state.Roadside)
        {
            await Workflow.ExecuteActivityAsync((MaintenanceActivities a) => a.HoldTruckInPlaceAsync(unit), ActivityProfiles.Local());
            Timeline("warn", "Truck is out of service at the roadside. Driver told to stay with the vehicle.");
            if (result.ActiveLoadId != null)
            {
                var position = await Workflow.ExecuteActivityAsync(
                    (MaintenanceActivities a) => a.GetTruckPositionAsync(unit), ActivityProfiles.Local());
                var notice = new TruckUnavailableNotice(unit, maintenanceId, state.Description, position.Lat, position.Lon);
                var loadWorkflowId = WorkflowIds.Load(result.ActiveLoadId);
                try
                {
                    await Workflow.GetExternalWorkflowHandle<LoadWorkflow>(loadWorkflowId)
                        .SignalAsync(wf => wf.TruckUnavailableAsync(notice));
                    state.RepoweredLoadId = result.ActiveLoadId;
                    Timeline("signal", $"Told load {result.ActiveLoadId} to re-power. Nothing downstream fails.");
                }
                catch (Exception e)
                {
                    Timeline("warn", $"Load {result.ActiveLoadId} could not be signaled: {e.Message}");
                }
            }
        }
        else if (result.Deferred)
        {
            state.DeferredForLoadId = result.ActiveLoadId;
            Timeline("info", $"Severity {state.Severity}: the truck finishes load {result.ActiveLoadId} first. It won't be offered new loads.");
        }
        else
        {
            Timeline("info", "Truck held for maintenance and removed from the available pool.");
        }
    }

    private async Task<ShopOption> ChooseShopAsync()
    {
        SetPhase(MaintenancePhase.Triage);
        var search = new ShopSearchRequest(state.Unit, state.Category, state.Severity, state.Roadside);
        state.Options = await Workflow.ExecuteActivityAsync(
            (MaintenanceActivities a) => a.FindShopOptionsAsync(search), ActivityProfiles.Local());
        Timeline("info", $"{state.Options.Count} shop options ranked ({state.Options.Count(o => o.IsPartner)} partner).");

        var request = new AdvisorRequest(
            state.Unit, state.Category, state.Severity, state.Roadside, state.Description, state.Options, settings.ApprovalThreshold);
        state.Recommendation = await RecommendAsync(request);
        var recommendation = state.Recommendation;
        var chosen = state.Options.FirstOrDefault(o => o.ShopId == recommendation.ShopId) ?? state.Options.First();
        Timeline("decision", $"{recommendation.Source} recommends {chosen.Name}. {recommendation.Rationale}");

        return recommendation.RequiresApproval ? await AwaitApprovalAsync(chosen, recommendation.ApprovalReason ?? "") : chosen;
    }

    /// <summary>The LLM is one activity among many: retried by Temporal, recorded in history, never re-billed on replay.</summary>
    private async Task<Recommendation> RecommendAsync(AdvisorRequest request)
    {
        try
        {
            var ai = await Workflow.ExecuteActivityAsync(
                (MaintenanceActivities a) => a.RecommendShopWithAiAsync(request), ActivityProfiles.Ai());
            if (ai != null)
            {
                return ai;
            }
        }
        catch (ActivityFailureException e)
        {
            Timeline("warn", $"AI advisor unavailable ({e.InnerException?.Message ?? e.Message}). Using the policy engine.");
        }

        return await Workflow.ExecuteActivityAsync(
            (MaintenanceActivities a) => a.RecommendShopWithPolicyAsync(request), ActivityProfiles.Local());
    }

    private async Task<ShopOption> AwaitApprovalAsync(ShopOption recommended, string reason)
    {
        state.Approval = new ApprovalInfo { Reason = reason, RequestedAt = Workflow.UtcNow };
        decision = null;
        SetPhase(MaintenancePhase.AwaitingApproval);
        SetAttention(true);
        Timeline("decision", $"Waiting for a fleet manager: {reason}");

        var decided = await Workflow.WaitConditionAsync(() => decision != null || cancelRequested, settings.Real(240));
        SetAttention(false);
        ThrowIfCancelled();

        var bestPartner = state.Options.Where(o => o.IsPartner).OrderBy(o => o.Score).FirstOrDefault();
        if (!decided)
        {
            // Timeout policy: a partner workshop within the safe range for this severity, otherwise the
            // recommendation. Never send a truck beyond its safe range without a person deciding that.
            var inRangePartner = state.Options.Where(o => o.IsPartner && o.Score < 100_000).OrderBy(o => o.Score).FirstOrDefault();
            var fallback = inRangePartner ?? recommended;
            state.Approval.Outcome = $"No decision in 4 h (simulated); policy chose {fallback.Name}";
            state.Approval.DecidedBy = "Policy (timeout)";
            state.Approval.DecidedAt = Workflow.UtcNow;
            Timeline("warn", $"No decision within 4 hours (simulated). Policy routes the truck to {fallback.Name}.");
            return fallback;
        }

        var d = decision!;
        state.Approval.DecidedAt = Workflow.UtcNow;
        state.Approval.DecidedBy = d.DecidedBy;
        state.Approval.Note = d.Note;
        var alternate = d.AlternateShopId != null ? state.Options.FirstOrDefault(o => o.ShopId == d.AlternateShopId) : null;
        if (d.Approve)
        {
            state.Approval.Outcome = alternate != null ? $"Approved with a change: {alternate.Name}" : "Approved";
            return alternate ?? recommended;
        }

        var replacement = alternate ?? bestPartner ?? recommended;
        state.Approval.Outcome = $"Rejected; rerouted to {replacement.Name}";
        return replacement;
    }

    private async Task<ShopOption> SourceShopAsync(ShopOption first, List<Func<Task>> compensations)
    {
        SetPhase(MaintenancePhase.Sourcing);
        var queue = new List<ShopOption> { first };
        queue.AddRange(state.Options.Where(o => o.ShopId != first.ShopId).OrderBy(o => o.Score).Take(3));
        var bestPartner = state.Options.Where(o => o.IsPartner).OrderBy(o => o.Score).FirstOrDefault();
        if (bestPartner != null && queue.All(o => o.ShopId != bestPartner.ShopId))
        {
            queue.Add(bestPartner);
        }

        foreach (var option in queue)
        {
            ThrowIfCancelled();
            // The attempt number makes every request unique, so asking the same shop twice is a new request.
            var requestId = $"{state.MaintenanceId}-{option.ShopId}-{state.VendorAttempts.Count + 1}";

            var attempt = new VendorAttempt
            {
                RequestId = requestId,
                ShopId = option.ShopId,
                ShopName = option.Name,
                IsPartner = option.IsPartner,
                RequestedAt = Workflow.UtcNow,
            };
            state.VendorAttempts.Add(attempt);
            compensations.Add(() => Workflow.ExecuteActivityAsync(
                (MaintenanceActivities a) => a.CancelShopRequestAsync(requestId), ActivityProfiles.Local()));

            var shopRequest = new ShopRequest(
                requestId, state.MaintenanceId, Workflow.Info.WorkflowId, option.ShopId, option.Name, option.IsPartner,
                state.Unit, state.Category, state.Severity, state.Roadside, option.EstimatedCost);
            await Workflow.ExecuteActivityAsync((MaintenanceActivities a) => a.RequestShopAsync(shopRequest), ActivityProfiles.Local());
            Timeline("integration", $"Service requested from {option.Name}.");

            var windowMinutes = option.IsPartner ? 30 : 120;
            var answered = await Workflow.WaitConditionAsync(
                () => vendorResponses.ContainsKey(requestId) || cancelRequested, settings.Real(windowMinutes));
            ThrowIfCancelled();

            if (!answered)
            {
                attempt.Outcome = "Timed out";
                attempt.RespondedAt = Workflow.UtcNow;
                Timeline("warn", $"{option.Name} did not respond within {windowMinutes / 60.0:0.#} h (simulated). Trying the next shop.");
                await Workflow.ExecuteActivityAsync((MaintenanceActivities a) => a.CancelShopRequestAsync(requestId), ActivityProfiles.Local());
                continue;
            }

            var response = vendorResponses[requestId];
            attempt.RespondedAt = Workflow.UtcNow;
            attempt.Note = response.Note;
            if (!response.Accepted)
            {
                attempt.Outcome = "Declined";
                Timeline("warn", $"{option.Name} declined{(string.IsNullOrWhiteSpace(response.Note) ? "" : $": {response.Note}")}.");
                continue;
            }

            attempt.Outcome = "Accepted";
            acceptedRequestId = requestId;
            Timeline("success", $"{option.Name} accepted. ETA {response.EtaHours:0.0} h.");
            return option;
        }

        // Nobody accepted. Hand it to a person, then try again starting with the shop they pick.
        Timeline("error", "No shop accepted the request. Escalated to the fleet manager.");
        var picked = await AwaitApprovalAsync(first, "No shop accepted the request. Pick a shop to try.");
        return await SourceShopAsync(picked, compensations);
    }

    private async Task<WorkOrderRef> UpsertWorkOrderAsync(string status)
    {
        var priority = state.Roadside ? "Critical" : state.Severity >= Severity.High ? "High" : "Medium";
        var upsert = new WorkOrderUpsert(
            Workflow.Info.WorkflowId,
            state.Unit,
            $"{state.Category} on unit {state.Unit}",
            $"{state.AnomalyCode}: {state.Description}",
            priority,
            status,
            state.SelectedShop?.Name,
            state.SelectedShop?.EstimatedCost,
            state.ActualCost);
        return await Workflow.ExecuteActivityAsync(
            (SalesforceActivities a) => a.UpsertWorkOrderAsync(upsert), ActivityProfiles.Integration(settings));
    }

    // ---------------------------------------------------------------- helpers

    private void ThrowIfCancelled()
    {
        if (cancelRequested)
        {
            throw new CaseCancelledException();
        }
    }

    private void SetPhase(MaintenancePhase phase)
    {
        state.Phase = phase;
        FleetVisibility.Upsert(settings, FleetSearchAttributes.Status.ValueSet(phase.ToString()));
    }

    private void SetAttention(bool needsAttention)
    {
        state.NeedsAttention = needsAttention;
        FleetVisibility.Upsert(settings, FleetSearchAttributes.NeedsAttention.ValueSet(needsAttention));
    }

    private void Timeline(string kind, string text) =>
        state.Timeline.AddCapped(new EntityEvent(Workflow.UtcNow, kind, text), 60);

    private MaintenanceResult Result(string outcome) => new(
        state.MaintenanceId,
        state.Unit,
        outcome,
        state.SelectedShop?.ShopId,
        state.SelectedShop?.IsPartner ?? false,
        state.ActualCost ?? 0,
        state.Savings);

    /// <summary>Internal control-flow exception for business cancellation. Never escapes the workflow.</summary>
    private sealed class CaseCancelledException : Exception
    {
    }
}
