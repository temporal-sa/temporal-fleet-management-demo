# Demo script

Step-by-step scenarios for the fleet control demo. Each one says how to start it, what happens, what to look for in the fleet UI, and where to see it in the Temporal UI. Setup is in the [README](README.md).

Most scenarios are a button in the fleet UI's **Demo runbook** (top right). The rest are switches in the same panel, or a command in a terminal.

## Before you start

1. Start everything with `scripts\start-all.ps1` (Windows) or `scripts/start-all.sh` (macOS and Linux).
2. Open the fleet UI at http://localhost:5173 and the Temporal UI at http://localhost:8233. Side by side works well.
3. Wait until the top bar shows four green worker pills, the map shows about 24 trucks, and the **Loads** tab has a handful of loads. This takes about 20 to 30 seconds.
4. Decide on background activity. The runbook's **Background** section has random anomalies and safety events. Leave them on for a busy fleet, or switch them off for a predictable run.

Time runs fast: one real second is three simulated minutes. A 2-hour wait takes 40 seconds.

## A quick tour of the UI

| Area | What it shows |
|---|---|
| Top bar | Simulated clock, fleet KPIs, savings from partner workshops, approvals waiting, duplicates absorbed, one pill per worker task queue, and banners for active failure switches |
| Map | Arrows are moving trucks, dots are stopped ones. Color shows status: green available, blue on a load, amber heading to or in a workshop, red broken down. Outlined squares are partner workshops; **Show general workshops** adds the rest. Click a truck to open it. |
| Side panel tabs | Trucks, Loads, Repairs, Drivers, Inbox (things waiting on a person) and Systems (Salesforce, TMS, Kafka and workshop activity) |
| Detail panels | State, timeline, and **Waiting on** (activities in progress, with attempt counts and the last failure). **Open in Temporal** opens that workflow's event history. |
| Event stream (bottom) | What the outside world did: telematics rules firing, geofence arrivals, workshop answers, Salesforce and TMS activity, and every signal sent to Temporal. Click a line to open the related item. |
| Demo runbook | Scenario buttons and failure switches |

---

## Scenarios

### 1. Explore a truck and its workflow

1. Click any blue truck on the map.
2. The panel shows the truck entity's status, current load, driver, its TMS record, live telemetry, and a timeline.
3. Click **Open in Temporal**. The history shows the workflow starting from a Salesforce record, `ReserveForLoad` and `MarkDispatched` arriving as Updates from a load workflow, and a TMS write after each status change.
4. Back in the fleet UI, open the **Trucks** tab, choose a filter such as **Shop**, and click **Same list in Temporal**. The Temporal UI opens with a visibility query like `FleetEntity="Truck" AND FleetStatus="InShop"`.

**Temporal concepts:** entity workflows, queries, Workflow Updates, custom search attributes, visibility queries.

### 2. Expensive repair needs a manager

1. Runbook, **Expensive repair needs a manager**, **Run**. The scenario picks an idle truck far from any partner engine workshop and overheats its engine.
2. Within seconds the repair case reaches **Awaiting approval** and the **Inbox** tab shows a badge.
3. Open the repair. Note the recommendation and its rationale, the workshop options (partner workshops are labeled), and the approval reason.
4. Either click **Approve**, choose a different workshop from the list and approve, or click **Reject, use a partner workshop**.
5. Try approving again before the panel refreshes. The toast reports that a decision was already recorded.
6. In the Temporal UI, find the `Decide` update in the workflow's history.

If nobody decides within 4 simulated hours (80 seconds), the policy picks for you and the timeline says so.

**Temporal concepts:** human in the loop, Workflow Update with a validator, durable timer as a timeout.

### 3. Slow tire leak on a loaded truck

1. Runbook, **Slow tire leak on a loaded truck**, **Run**.
2. The repair case goes to **After delivery**. The timeline explains that the truck finishes its load first and won't be offered new loads. The truck row shows "with repair pending".
3. When the load delivers, the truck entity signals the case. It ranks workshops, usually picks a partner, gets the booking accepted, and routes the truck there.
4. When the repair closes, **Saved at partners** in the top bar goes up by the difference against a typical general workshop invoice. The repair panel's **Cost** section shows the numbers.

**Temporal concepts:** cross-workflow signals, waiting on a condition with no polling.

### 4. Engine overheating on a loaded truck

1. Runbook, **Engine overheating on a loaded truck**, **Run**.
2. The coolant gauge on the truck panel climbs. When it passes 228°F, the stream rule `SPN110-COOLANT-HIGH` fires (see the event stream) and the truck entity opens a repair case.
3. If a related anomaly fires while the case is open, the truck timeline says it was merged into the open case rather than opening a duplicate.
4. The truck refuses new loads until the repair closes.

**Temporal concepts:** an entity as the single place where decisions about that entity are made.

### 5. Preventive maintenance comes due

1. Runbook, **Preventive maintenance comes due**, **Run**. The truck's odometer passes its PM-A interval.
2. A low-severity case opens. It waits for the current load if there is one, then books a partner workshop.

Truck 41205 starts about 780 miles from its PM-A, so this also tends to happen on its own during a session.

### 6. Workshops that don't answer

1. Runbook, switch on **General workshops stop answering**.
2. Run **Expensive repair needs a manager** and approve the general workshop it recommends.
3. The service request stays **Pending**. After 2 simulated hours (40 seconds), the timeline says the workshop did not respond and the next one is asked. A partner workshop is always the last fallback, and partner workshops answer quickly.
4. In the Temporal UI, find the timer that fired and the `CancelShopRequest` activity that withdrew the first request.

You can also answer for a workshop from the repair panel (**Accept** or **Decline**). **General workshops decline** makes every general workshop decline.

**Temporal concepts:** durable timers, escalation logic as ordinary code.

### 7. Duplicate and out-of-order events

1. Runbook, switch on **Systems send duplicate events**, then run any maintenance scenario through to completion.
2. The workshop sends its completion twice. The repair timeline says the duplicate was ignored and the work order closes once. **Duplicates absorbed** in the top bar goes up.
3. On a closed repair, **Send the completion again** sends one more duplicate on demand.
4. **Systems** tab, **Salesforce work orders**: one work order per case. The workflow upserts it at every step, keyed by the workflow ID as an external ID, so retries never create a second record.

**Temporal concepts:** idempotent signal handling, idempotent activities.

### 8. Cancel a repair case

1. Open any repair case before it reaches **In repair**.
2. Click **Cancel case**.
3. The timeline shows the compensations running in reverse order: workshop requests withdrawn, the truck's hold released, the work order closed as Canceled.

**Temporal concepts:** saga compensation.

### 9. Loaded truck breaks down

1. Runbook, **Loaded truck breaks down**, **Run**. The truck turns red on the map with a pulsing ring.
2. Event stream: the stream rule `SPN100-ENGINE-SHUTDOWN` fired and was signaled to the truck.
3. Open the repair case. The timeline says the load was told to re-power.
4. Open the load. Planning runs again: a replacement truck is assigned and its first stop is **Roadside trailer transfer**. Watch it drive to the broken truck.
5. Back on the repair: a roadside workshop accepts, and the broken truck is towed (red dashed route on the map).
6. In the Temporal UI, open the load workflow and find the `TruckUnavailable` signal and the second planning cycle.

**Temporal concepts:** workflows coordinating through signals, re-planning as normal control flow.

### 10. Tire blowout on the road

1. Runbook, **Tire blowout on the road**, **Run**.
2. Same roadside path as a breakdown: the truck is taken out of service, its load is re-powered, and the nearest capable workshop is booked. Roadside repairs skip approval.
3. The truck panel's **TMS record** shows DOWN within seconds.

### 11. Offer a load to a truck in the shop

Needs a truck with an open repair. Run scenario 2, 3 or 9 first.

1. Runbook, **Dispatcher offers a load to a truck in the shop**, **Run**. The UI opens the new load.
2. Planning log: the truck declined, with the reason (it has an open maintenance case), and the next candidate was assigned.

**Temporal concepts:** Update validators as the authority on an entity's rules.

### 12. A driver who is still onboarding

This usually happens on its own early in a session. Trucks 41223 (Omaha) and 41224 (Dallas) belong to drivers who start in onboarding.

1. Open a load planned near Omaha or Dallas, or look through the **Loads** tab.
2. The planning log shows the driver declining ("still onboarding, 2 of 5 steps done..."), then "Released the hold on unit 41223", then the next candidate.

**Temporal concepts:** saga compensation within planning; state owned by the driver entity.

### 13. Someone edits a truck by hand in the TMS

1. Runbook, **Someone edits a truck by hand in the TMS**, **Run**.
2. The truck row shows **TMS mismatch**, and the truck panel's TMS card turns red.
3. The scenario triggers the reconciliation run. The truck timeline shows a request to publish again, then "Conflict resolved". The card turns green.
4. **Systems** tab, **Equipment reconciliation** lists the conflict. **Open last sync run** shows the Schedule-started workflow in Temporal.

Every TMS write carries a status version, and the TMS ignores writes older than what it has. The truck timeline shows these as "TMS updated: ASSIGNED (v2)" and similar.

**Temporal concepts:** Schedules, single-writer entities, reconciliation.

### 14. Hard brake while driving, then a rest stop

1. Runbook, **Hard brake while driving**, **Run**. The UI opens the driver.
2. The timeline says the coaching call waits for the next stop. The **Drivers** tab shows **call held**.
3. Runbook, **Driver pulls into a rest area**, **Run**. It picks a driver with a held call.
4. The ELD reports off duty and the driver entity starts a coaching workflow. Open it: a Salesforce case opens and the IVR calls the driver.
5. The driver presses 1 most of the time. Otherwise the workflow tries three times, 20 simulated minutes apart, then escalates to the **Inbox**. You can press 1 for the driver from the coaching panel, and **Mark resolved** closes an escalated one.

Switch on **Drivers do not answer calls** to force the escalation path.

**Temporal concepts:** events held on an entity until the right moment, child workflows, retries with durable waits.

### 15. New driver hired in Salesforce

1. Runbook, **New driver hired in Salesforce**, **Run**.
2. Event stream: Salesforce created the Driver__c record and sent a change event to the fleet API, which started the driver's workflow.
3. The driver panel shows the onboarding steps advancing, each on a durable timer. Each step updates Salesforce, and completion creates the driver's TMS profile.
4. A duplicate change event, or the scheduled sync finding the same record, cannot start a second workflow, because the workflow ID is the driver ID.

### 16. TMS outage

1. Runbook, switch on **TMS outage**. The top bar shows "TMS down".
2. Open an active load. **Waiting on** shows `RecordLoadMilestone` with a rising attempt count and the last failure (a 503 from the TMS). Trucks keep moving and geofence signals keep arriving.
3. **Systems** tab, **TMS calls**: a column of 503s.
4. Wait a minute or two, then switch the outage off. Within about ten seconds, the waiting milestones are recorded in order, each exactly once.

Truck status writes use a separate lane, so an outage never stops a truck from opening a repair case.

**Temporal concepts:** activity retry policies, ordered and exactly-once side effects.

### 17. Salesforce outage, rate limiting and expired sessions

- **Salesforce outage** switch: new repair cases pause at the work-order step and continue when Salesforce recovers, with one work order per case.
- **Salesforce rate limiting** switch: `REQUEST_LIMIT_EXCEEDED` is treated as retryable, and backoff handles it.
- **Salesforce sessions expire** scenario: the next call gets a 401, re-authenticates and replays. The workflow never sees it.

The **Systems** tab, **Salesforce API calls** shows each case.

### 18. Kill a worker

1. Close the **Worker: maintenance** window, or run `scripts\stop-worker.ps1 maintenance` / `scripts/stop-worker.sh maintenance`. The stop script is a hard kill.
2. The **Maintenance** pill turns red.
3. Run any maintenance scenario. The truck entity, on a different worker, still opens the case, but the case makes no progress. In the Temporal UI, the new maintenance workflow has a pending workflow task with no worker to take it.
4. Start the worker again: `scripts\worker.ps1 maintenance` or `scripts/worker.sh maintenance`. The pill turns green and every paused case continues from the step it was on.

Stopping **integrations** instead pauses every Salesforce and TMS call across all workflows, while each workflow keeps running up to its next integration step.

**Temporal concepts:** durable execution, task queues, independent worker scaling.

### 19. Continue-as-new

Nothing to start: watch for it. Truck and driver workflows run for the life of the truck or driver. After a few hundred messages, or when Temporal suggests it, they continue as new: the run closes, and a new run with the same workflow ID and the same state starts. The truck timeline says "Continued as new (run 2)", and the Temporal UI shows the earlier run as Continued As New.

Trucks that are reserved and released often (41223 and 41224 early in a session, or any truck while loads are waiting for capacity) get there first.

### 20. Tests and safe changes

1. `dotnet test` runs a full repair case on the time-skipping test server, including a 2-hour timeout, in a few seconds.
2. [docs/live-change/README.md](docs/live-change/README.md) walks through changing a workflow while cases are in flight: export a history, make an unsafe change and watch the replay test fail, then patch it and watch it pass.

---

## Suggested running orders

| Session | Scenarios |
|---|---|
| 15 minutes | 1, 2, 9, 16, 18 |
| 30 minutes | 1, 2, 3, 6, 9, 11, 14, 16, 18 |
| 60 minutes | All of them |

## Failure switches

| Switch | Effect |
|---|---|
| TMS outage | Every TMS endpoint returns 503 |
| Salesforce outage | Salesforce login and API return 503 |
| Salesforce rate limiting | Salesforce API returns 403 `REQUEST_LIMIT_EXCEEDED` |
| General workshops stop answering | General workshops never respond to booking requests |
| General workshops decline | General workshops decline every request |
| Workshops report completion by hand | Repairs only finish when you click **Shop: mark repair complete** |
| Systems send duplicate events | Completions and safety events are delivered twice |
| Drivers do not answer calls | The IVR never gets an acknowledgment |
| Background anomalies / safety events | Random faults and driving events every few minutes |
| Pause the simulation | Trucks stop moving; workflow timers keep running |
| Tender new loads automatically | Keeps about a dozen loads moving |

Scenarios can also be triggered from a terminal: `POST http://localhost:5100/api/scenarios/{name}` with an optional body `{"unit":"41207"}` or `{"driverId":"D2007"}`. Names: `engine-approval`, `tire-leak`, `engine-anomaly`, `def-fault`, `pm-due`, `tire-blowout`, `breakdown`, `assign-to-shop-truck`, `tms-conflict`, `hard-brake`, `driver-break`, `new-hire`, `expire-salesforce-tokens`, `run-sync`, `tender-load`.

## Resetting and recovering

- **Clean slate:** `stop-all`, then `start-all`. The dev server is in-memory.
- **Too much happening:** switch off background anomalies and safety events, or pause the simulation.
- **A scenario says "no suitable truck":** wait for trucks to pick up loads, or open a truck and use the buttons on its panel.
- **The map stops moving:** check the External systems window or `.logs/external-systems.log`.
- **Lists show "stale":** the entities worker is down or restarting. Expected during scenario 18.
