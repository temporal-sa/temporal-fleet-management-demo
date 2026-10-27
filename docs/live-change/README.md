# Live change: shipping a workflow change with cases in flight

What happens when you change a long-running process while hundreds of cases are in the middle of it? This exercise shows the failure you would get, the test that catches it before deploy, and the fix.

## The change

Product asks: "When a shop accepts a repair, call the driver with the shop's name so they know where they're going."

In `src/Fleet.Workflows/Workflows/MaintenanceWorkflow.workflow.cs`, inside `SourceShopAsync`, find:

```csharp
            attempt.Outcome = "Accepted";
            acceptedRequestId = requestId;
            Timeline("success", $"{option.Name} accepted. ETA {response.EtaHours:0.0} h.");
            return option;
```

## Step 1: export a real history

With the demo running, let a repair case complete (a tire leak on an idle truck is quickest), then export it:

```bash
scripts/export-history.sh maint-41209-001        # or scripts\export-history.ps1 maint-41209-001
dotnet test --filter ReplayTests                 # passes: current code matches the history
```

## Step 2: make the change the naive way

```csharp
            attempt.Outcome = "Accepted";
            acceptedRequestId = requestId;
            Timeline("success", $"{option.Name} accepted. ETA {response.EtaHours:0.0} h.");

            var notice = new CoachingCall(
                $"{state.MaintenanceId}-shop-notice", Workflow.Info.WorkflowId, "dispatch", "",
                $"Unit {state.Unit}: your repair is booked at {option.Name}.");
            await Workflow.ExecuteActivityAsync((SafetyActivities a) => a.PlaceCoachingCallAsync(notice), ActivityProfiles.Local());

            return option;
```

```bash
dotnet test --filter ReplayTests                 # fails with a nondeterminism error
```

The recorded history says the next command after acceptance was the work-order upsert; the new code issues a call first. Any case past this point when the new worker deploys would hit the same error on its next replay and stop making progress until fixed. The test caught it before the deploy did. This is the check to put in CI, especially when AI writes the change.

If you want to see it live instead: restart the maintenance worker with this code while a case is past acceptance (switch on manual repair completion so cases wait in repair). The Temporal UI shows the workflow task failing with a nondeterminism error. Nothing is lost: put the patch in (step 3), restart the worker, and the case continues.

## Step 3: make the change safely

```csharp
            if (Workflow.Patched("notify-driver-of-shop"))
            {
                var notice = new CoachingCall(
                    $"{state.MaintenanceId}-shop-notice", Workflow.Info.WorkflowId, "dispatch", "",
                    $"Unit {state.Unit}: your repair is booked at {option.Name}.");
                await Workflow.ExecuteActivityAsync((SafetyActivities a) => a.PlaceCoachingCallAsync(notice), ActivityProfiles.Local());
            }
```

```bash
dotnet test --filter ReplayTests                 # passes
```

New cases take the new path and record a marker. Cases that were already past this point replay the old path. Once no running case predates the patch (find them with the visibility query `WorkflowType = "MaintenanceWorkflow" AND ExecutionStatus = "Running" AND TemporalChangeVersion IS NULL`), replace `Workflow.Patched` with `Workflow.DeprecatePatch`, and later remove it.

## Takeaways

- Workflow code must replay the same way for histories already recorded. Activities are unaffected and can change freely.
- The replay test makes this mechanical: export a few real histories per workflow type into `tests/Fleet.Tests/histories`, and CI fails any change that would break them. This is especially useful when code is generated or reviewed quickly.
- For larger changes, Worker Versioning pins running workflows to the code version they started on, so old and new workers run side by side and the old ones drain.
