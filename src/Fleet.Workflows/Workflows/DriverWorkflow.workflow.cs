using Fleet.Core;
using Fleet.Workflows.Activities;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Fleet.Workflows;

/// <summary>
/// Entity workflow: one per driver, ID "driver-{id}".
///
/// Two patterns live here:
/// 1. Onboarding runs inside the driver entity, and the reservation validator rejects dispatch until it
///    finishes. Other workflows never have to ask a separate system whether hiring is still in progress.
/// 2. A safety event is held on the driver until the ELD reports a stop, then the driver entity starts the
///    coaching call. One signal per event, delivered to the one driver it concerns, with no central poller.
/// </summary>
[Workflow]
public class DriverWorkflow
{
    private const int MaxEventsPerRun = 400;

    private readonly DriverState state;
    private readonly DemoSettings settings;
    private bool coachingRequested;
    private int handledThisRun;

    [WorkflowInit]
    public DriverWorkflow(DriverInput input)
    {
        settings = input.Settings;
        if (input.CarryOver != null)
        {
            state = input.CarryOver;
            state.RunNumber++;
            return;
        }

        state = new DriverState
        {
            DriverId = input.DriverId,
            Name = input.Name,
            HomeHub = input.HomeHub,
            Endorsements = input.Endorsements,
            Employment = input.Employment,
            Phone = input.Phone,
            SalesforceId = input.SalesforceId,
            CurrentUnit = input.PairedUnit,
        };

        if (input.Employment == DriverEmployment.Onboarding)
        {
            for (var i = 0; i < FleetSeed.OnboardingSteps.Length; i++)
            {
                state.Onboarding.Add(new OnboardingStep
                {
                    Name = FleetSeed.OnboardingSteps[i],
                    SimMinutes = FleetSeed.OnboardingStepSimMinutes[i],
                });
            }
        }
    }

    [WorkflowQuery]
    public DriverState GetState() => state;

    [WorkflowRun]
    public async Task RunAsync(DriverInput input)
    {
        if (state.DutySince == default)
        {
            state.DutySince = Workflow.UtcNow;
        }

        FleetVisibility.Upsert(settings, 
            FleetSearchAttributes.Entity.ValueSet("Driver"),
            FleetSearchAttributes.Unit.ValueSet(state.CurrentUnit ?? "none"),
            FleetSearchAttributes.Status.ValueSet(StatusLabel()),
            FleetSearchAttributes.NeedsAttention.ValueSet(false));

        if (input.CarryOver == null)
        {
            AddEvent("info", state.Employment == DriverEmployment.Onboarding
                ? "Driver entity started from a new hire in Salesforce."
                : "Driver entity started from Salesforce.");
        }
        else
        {
            AddEvent("info", $"Continued as new (run {state.RunNumber}).");
        }

        if (state.Employment == DriverEmployment.Onboarding)
        {
            await RunOnboardingAsync();
        }

        while (true)
        {
            await Workflow.WaitConditionAsync(() => coachingRequested || ShouldContinueAsNew);

            if (coachingRequested)
            {
                coachingRequested = false;
                await StartCoachingAsync();
            }

            if (ShouldContinueAsNew)
            {
                await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
                if (coachingRequested)
                {
                    continue;
                }

                var next = new DriverInput { DriverId = state.DriverId, Settings = settings, CarryOver = state };
                throw Workflow.CreateContinueAsNewException((DriverWorkflow wf) => wf.RunAsync(next));
            }
        }
    }

    private bool ShouldContinueAsNew => handledThisRun >= MaxEventsPerRun || Workflow.ContinueAsNewSuggested;

    // ---------------------------------------------------------------- signals

    [WorkflowSignal(Signals.DutyStatusChanged)]
    public async Task DutyStatusChangedAsync(DutyStatusChange change)
    {
        handledThisRun++;
        state.Hos = change.Hos;
        if (change.Unit != null)
        {
            state.CurrentUnit = change.Unit;
        }

        if (state.Duty != change.Status)
        {
            state.Duty = change.Status;
            state.DutySince = Workflow.UtcNow;
            AddEvent("eld", $"{DescribeDuty(change.Status)} ({change.Reason}).");
            UpsertStatus();
        }

        var stopped = change.Status is DutyStatus.OffDuty or DutyStatus.SleeperBerth;
        if (stopped && state.PendingSafetyEvents.Count > 0 && !coachingRequested)
        {
            coachingRequested = true;
            AddEvent("safety", "Driver is stopped. Starting the held coaching call.");
        }
    }

    [WorkflowSignal(Signals.SafetyEvent)]
    public async Task SafetyEventAsync(SafetyEvent evt)
    {
        handledThisRun++;
        if (state.SeenSafetyEventIds.Contains(evt.EventId))
        {
            AddEvent("dedupe", $"Duplicate safety event {evt.EventId} ignored.");
            return;
        }

        state.SeenSafetyEventIds.Add(evt.EventId);
        if (state.SeenSafetyEventIds.Count > 50)
        {
            state.SeenSafetyEventIds.RemoveAt(0);
        }

        state.SafetyEventsTotal++;
        state.PendingSafetyEvents.Add(evt);
        var what = $"{Describe(evt.Type)} at {evt.SpeedMph:0} mph near {evt.Location}.";
        if (state.Duty == DutyStatus.Driving)
        {
            AddEvent("safety", $"{what} Driver is driving, so the coaching call waits for the next stop.");
        }
        else
        {
            AddEvent("safety", $"{what} Driver is stopped; calling now.");
            coachingRequested = true;
        }

        UpsertStatus();
    }

    [WorkflowSignal(Signals.SyncMasterData)]
    public async Task SyncMasterDataAsync(DriverMasterData data)
    {
        handledThisRun++;
        state.SalesforceId = data.SalesforceId ?? state.SalesforceId;
        state.Name = data.Name;
        state.HomeHub = data.HomeHub;
        state.Endorsements = data.Endorsements;
        state.Phone = data.Phone;
        AddEvent("integration", "Master data refreshed from Salesforce.");
    }

    // ---------------------------------------------------------------- updates

    [WorkflowUpdateValidator(nameof(ReserveForLoadAsync))]
    public void ValidateReserveForLoad(ReserveDriverRequest request)
    {
        if (state.CurrentLoadId == request.LoadId)
        {
            return;
        }

        if (state.Employment == DriverEmployment.Onboarding)
        {
            var done = state.Onboarding.Count(s => s.Status == "Done");
            var current = state.Onboarding.FirstOrDefault(s => s.Status != "Done")?.Name ?? "final checks";
            throw new ApplicationFailureException(
                $"{state.Name} is still onboarding ({done} of {state.Onboarding.Count} steps done, now on: {current}).",
                errorType: "DriverOnboarding");
        }

        if (state.Employment != DriverEmployment.Active)
        {
            throw new ApplicationFailureException($"{state.Name} is not active.", errorType: "DriverInactive");
        }

        if (state.CurrentLoadId != null)
        {
            throw new ApplicationFailureException(
                $"{state.Name} is already on load {state.CurrentLoadId}.", errorType: "DriverUnavailable");
        }

        var missing = request.RequiredEndorsements.Where(e => !state.Endorsements.Contains(e)).ToList();
        if (missing.Count > 0)
        {
            throw new ApplicationFailureException(
                $"{state.Name} does not have the {string.Join(", ", missing)} endorsement.", errorType: "DriverUnqualified");
        }

        if (state.Hos.CycleMinutesLeft < 120)
        {
            throw new ApplicationFailureException(
                $"{state.Name} has only {state.Hos.CycleMinutesLeft / 60:0.0} h left in the 70-hour cycle.", errorType: "DriverOutOfHours");
        }
    }

    [WorkflowUpdate]
    public async Task<ReserveDriverResult> ReserveForLoadAsync(ReserveDriverRequest request)
    {
        handledThisRun++;
        if (state.CurrentLoadId != request.LoadId)
        {
            state.CurrentLoadId = request.LoadId;
            state.CurrentUnit = request.Unit;
            AddEvent("dispatch", $"Assigned to load {request.LoadId} on unit {request.Unit}.");
            UpsertStatus();
        }

        return new ReserveDriverResult(state.DriverId, request.LoadId);
    }

    [WorkflowUpdate]
    public async Task<bool> ReleaseFromLoadAsync(string loadId)
    {
        handledThisRun++;
        if (state.CurrentLoadId != loadId)
        {
            return false;
        }

        state.CurrentLoadId = null;
        state.LoadsCompleted++;
        AddEvent("dispatch", $"Released from load {loadId}.");
        UpsertStatus();
        return true;
    }

    // ---------------------------------------------------------------- internals

    private async Task RunOnboardingAsync()
    {
        AddEvent("info", "Onboarding started.");
        var total = state.Onboarding.Count;
        for (var i = 0; i < total; i++)
        {
            var step = state.Onboarding[i];
            if (step.Status == "Done")
            {
                continue;
            }

            step.Status = "Running";
            UpsertStatus();

            var progress = new DriverOnboardingUpdate(state.DriverId, "Onboarding", step.Name, i, total);
            await Workflow.ExecuteActivityAsync(
                (SalesforceActivities a) => a.UpdateDriverOnboardingAsync(progress),
                ActivityProfiles.Integration(settings));

            // A durable timer stands in for waiting on the background-check vendor, the clinic, and so on.
            // A worker restart in the middle of this costs nothing; the timer lives on the server.
            await Workflow.DelayAsync(settings.Real(step.SimMinutes));

            step.Status = "Done";
            step.CompletedAt = Workflow.UtcNow;
            AddEvent("success", $"{step.Name} complete ({i + 1} of {total}).");
        }

        state.Employment = DriverEmployment.Active;
        var done = new DriverOnboardingUpdate(state.DriverId, "Active", null, total, total);
        await Workflow.ExecuteActivityAsync(
            (SalesforceActivities a) => a.UpdateDriverOnboardingAsync(done),
            ActivityProfiles.Integration(settings));

        var profile = new TmsDriverProfile(state.DriverId, state.Name, "ACTIVE", state.HomeHub);
        await Workflow.ExecuteActivityAsync(
            (TmsActivities a) => a.UpsertDriverProfileAsync(profile),
            ActivityProfiles.Integration(settings));

        AddEvent("success", "Onboarding complete. Salesforce and TMS updated; the driver can now be dispatched.");
        UpsertStatus();
    }

    private async Task StartCoachingAsync()
    {
        if (state.PendingSafetyEvents.Count == 0)
        {
            return;
        }

        var events = state.PendingSafetyEvents.ToList();
        state.PendingSafetyEvents.Clear();
        state.CoachingSequence++;
        var coachingId = WorkflowIds.Coaching(state.DriverId, state.CoachingSequence);
        var input = new SafetyCoachingInput
        {
            CoachingId = coachingId,
            DriverId = state.DriverId,
            DriverName = state.Name,
            Phone = state.Phone,
            Events = events,
            Settings = settings,
        };

        try
        {
            await Workflow.StartChildWorkflowAsync(
                (SafetyCoachingWorkflow wf) => wf.RunAsync(input),
                new()
                {
                    Id = coachingId,
                    TaskQueue = TaskQueues.Maintenance,
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                });
            state.CoachingWorkflowIds.Add(coachingId);
            if (state.CoachingWorkflowIds.Count > 10)
            {
                state.CoachingWorkflowIds.RemoveAt(0);
            }

            AddEvent("safety", $"Coaching call {coachingId} started for {events.Count} event(s).");
        }
        catch (Exception e)
        {
            AddEvent("warn", $"Coaching call was not started: {e.Message}");
        }

        UpsertStatus();
    }

    private string StatusLabel()
    {
        if (state.Employment == DriverEmployment.Onboarding)
        {
            return "Onboarding";
        }

        if (state.Employment != DriverEmployment.Active)
        {
            return "Inactive";
        }

        return state.CurrentLoadId != null ? "OnLoad" : state.Duty.ToString();
    }

    private void UpsertStatus() =>
        FleetVisibility.Upsert(settings, 
            FleetSearchAttributes.Status.ValueSet(StatusLabel()),
            FleetSearchAttributes.Unit.ValueSet(state.CurrentUnit ?? "none"));

    private void AddEvent(string kind, string text) =>
        state.RecentEvents.AddCapped(new EntityEvent(Workflow.UtcNow, kind, text), 30);

    private static string DescribeDuty(DutyStatus duty) => duty switch
    {
        DutyStatus.OffDuty => "Off duty",
        DutyStatus.SleeperBerth => "Sleeper berth",
        DutyStatus.Driving => "Driving",
        DutyStatus.OnDuty => "On duty, not driving",
        _ => duty.ToString(),
    };

    private static string Describe(SafetyEventType type) => type switch
    {
        SafetyEventType.HardBrake => "Hard brake",
        SafetyEventType.Speeding => "Speeding",
        SafetyEventType.FollowingDistance => "Following too close",
        SafetyEventType.RollStability => "Roll stability event",
        _ => type.ToString(),
    };
}
