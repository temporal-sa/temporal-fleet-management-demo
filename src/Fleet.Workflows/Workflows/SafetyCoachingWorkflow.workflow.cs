using Fleet.Core;
using Fleet.Workflows.Activities;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Fleet.Workflows;

/// <summary>
/// Calls a driver about held safety events. Started by the driver entity only once the driver is stopped.
/// Three call attempts with durable waits between them, then escalation to a safety manager (human task).
/// </summary>
[Workflow]
public class SafetyCoachingWorkflow
{
    private readonly SafetyCoachingState state;
    private readonly DemoSettings settings;
    private readonly SafetyCoachingInput input;
    private readonly SortedSet<string> acknowledgedCalls = new();
    private CoachingResolution? resolution;

    [WorkflowInit]
    public SafetyCoachingWorkflow(SafetyCoachingInput input)
    {
        this.input = input;
        settings = input.Settings;
        state = new SafetyCoachingState
        {
            CoachingId = input.CoachingId,
            DriverId = input.DriverId,
            DriverName = input.DriverName,
            Events = input.Events,
        };
    }

    [WorkflowQuery]
    public SafetyCoachingState GetState() => state;

    [WorkflowRun]
    public async Task<string> RunAsync(SafetyCoachingInput runInput)
    {
        state.StartedAt = Workflow.UtcNow;
        FleetVisibility.Upsert(settings, 
            FleetSearchAttributes.Entity.ValueSet("Coaching"),
            FleetSearchAttributes.Status.ValueSet(state.Status.ToString()),
            FleetSearchAttributes.NeedsAttention.ValueSet(false));

        var summary = string.Join(", ", state.Events.Select(e => $"{e.Type} at {e.SpeedMph:0} mph"));
        Timeline("info", $"{state.Events.Count} safety event(s) to review with {state.DriverName}: {summary}.");

        var caseRequest = new SafetyCaseUpsert(
            Workflow.Info.WorkflowId, state.DriverId, $"Safety coaching for {state.DriverName}", summary, "Working", "Medium");
        state.CaseId = await Workflow.ExecuteActivityAsync(
            (SalesforceActivities a) => a.UpsertSafetyCaseAsync(caseRequest), ActivityProfiles.Integration(settings));
        Timeline("integration", $"Salesforce case {state.CaseId} opened.");

        var acknowledged = false;
        for (var attempt = 1; attempt <= 3 && !acknowledged; attempt++)
        {
            state.CallAttempts = attempt;
            SetStatus(CoachingStatus.Calling);
            var callId = $"{Workflow.Info.WorkflowId}-call{attempt}";
            var call = new CoachingCall(
                callId, Workflow.Info.WorkflowId, state.DriverId, input.Phone,
                $"Hi {state.DriverName}, this is fleet safety about a {summary} event. Press 1 to confirm you heard this message.");
            await Workflow.ExecuteActivityAsync((SafetyActivities a) => a.PlaceCoachingCallAsync(call), ActivityProfiles.Local());
            Timeline("integration", $"Calling {state.DriverName} (attempt {attempt} of 3).");

            acknowledged = await Workflow.WaitConditionAsync(() => acknowledgedCalls.Contains(callId), settings.Real(30));
            if (!acknowledged && attempt < 3)
            {
                Timeline("warn", $"No answer on attempt {attempt}. Calling again in 20 minutes (simulated).");
                await Workflow.DelayAsync(settings.Real(20));
            }
        }

        if (acknowledged)
        {
            SetStatus(CoachingStatus.Acknowledged);
            Timeline("success", "Driver acknowledged the coaching message.");
        }
        else
        {
            state.NeedsAttention = true;
            SetStatus(CoachingStatus.Escalated);
            Timeline("warn", "Three calls went unanswered. Escalated to the safety manager.");
            await Workflow.WaitConditionAsync(() => resolution != null);
            state.ResolvedBy = resolution!.ResolvedBy;
            state.NeedsAttention = false;
            SetStatus(CoachingStatus.Resolved);
            Timeline("success", $"Resolved by {resolution.ResolvedBy}{(string.IsNullOrWhiteSpace(resolution.Note) ? "" : $": {resolution.Note}")}.");
        }

        var close = caseRequest with { Status = "Closed" };
        await Workflow.ExecuteActivityAsync(
            (SalesforceActivities a) => a.UpsertSafetyCaseAsync(close), ActivityProfiles.Integration(settings));
        Timeline("integration", "Salesforce case closed.");

        await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
        return state.Status.ToString();
    }

    [WorkflowSignal(Signals.CallAcknowledged)]
    public async Task CallAcknowledgedAsync(string callId)
    {
        acknowledgedCalls.Add(callId);
    }

    [WorkflowUpdateValidator(nameof(ResolveAsync))]
    public void ValidateResolve(CoachingResolution r)
    {
        if (state.Status != CoachingStatus.Escalated)
        {
            throw new ApplicationFailureException($"Coaching is {state.Status}, not escalated.", errorType: "NotEscalated");
        }

        if (resolution != null)
        {
            throw new ApplicationFailureException("Already resolved.", errorType: "AlreadyResolved");
        }
    }

    [WorkflowUpdate]
    public async Task<string> ResolveAsync(CoachingResolution r)
    {
        resolution = r;
        return "Resolved";
    }

    private void SetStatus(CoachingStatus status)
    {
        state.Status = status;
        FleetVisibility.Upsert(settings, 
            FleetSearchAttributes.Status.ValueSet(status.ToString()),
            FleetSearchAttributes.NeedsAttention.ValueSet(state.NeedsAttention));
    }

    private void Timeline(string kind, string text) =>
        state.Timeline.AddCapped(new EntityEvent(Workflow.UtcNow, kind, text), 40);
}
