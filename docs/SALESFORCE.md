# Connecting to a real Salesforce org

The workers already use a real Salesforce REST client (`src/Fleet.Integrations/SalesforceRestClient.cs`). In the demo it points at the mock org in Fleet.ExternalSystems. Pointing it at a sandbox is configuration plus a few fields in the org.

## How the integration works

- **Auth:** OAuth 2.0 client credentials flow. The client gets a token from `{LoginUrl}/services/oauth2/token`, caches it, and on a 401 refreshes once and replays the request.
- **Reads:** SOQL through `/services/data/vXX.X/query`, following `nextRecordsUrl` for large results.
- **Writes:** upsert by external ID (`PATCH /sobjects/{Object}/{ExternalIdField}/{value}`). The external ID is the Temporal workflow ID, so a retried activity updates the same record instead of creating a second one. This is what makes "retry forever" safe.
- **Errors:** 5xx, timeouts, `REQUEST_LIMIT_EXCEEDED`, `SERVER_UNAVAILABLE` and `UNABLE_TO_LOCK_ROW` are retryable; Temporal backs off. Validation errors (`INVALID_FIELD`, `REQUIRED_FIELD_MISSING` and so on) fail the activity immediately with the Salesforce message.
- **Isolation:** Salesforce and TMS activities run on the `fleet-integrations` task queue, so their workers can be scaled, rate limited (`MaxTaskQueueActivitiesPerSecond` on the worker options caps API usage across every workflow) or paused independently.
- **Inbound:** new Driver__c records reach the fleet API as change events and start driver workflows. A scheduled sync is the backstop.

## Steps

1. **Create an External Client App (or Connected App)** with OAuth enabled, the "Client Credentials Flow" enabled, and a run-as integration user with API access. Note the consumer key and secret.
2. **Find your My Domain URL** (for example `https://yourco--fleetdemo.sandbox.my.salesforce.com`). The client credentials flow requires it; `login.salesforce.com` will not work.
3. **Create or map the objects and fields.** The demo uses these names. Either create them in a sandbox, or change the object and field names in `src/Fleet.Workflows/Activities/SalesforceActivities.cs` to match your existing model.

   | Object | Fields |
   |---|---|
   | `Equipment__c` (custom) | `Unit_Number__c` (Text, External ID, Unique), `Home_Terminal__c`, `Make__c`, `Model__c`, `Year__c` (Number), `VIN__c`, `Odometer__c` (Number), `PM_Due_Odometer__c` (Number), `Status__c` (Picklist with Active) |
   | `Driver__c` (custom) | `Driver_Id__c` (Text, External ID, Unique), `Home_Terminal__c`, `Hire_Status__c` (Picklist: Onboarding, Active, Terminated), `Endorsements__c` (semicolon list), `Phone__c`, `Paired_Unit__c`, `Onboarding_Step__c`, `Onboarding_Progress__c` |
   | `WorkOrder` (standard) | `Temporal_Workflow_Id__c` (Text, External ID, Unique), `Unit_Number__c`, `Shop_Name__c`, `Estimated_Cost__c` (Currency), `Actual_Cost__c` (Currency); Status values New, Scheduled, In Progress, Completed, Canceled |
   | `Case` (standard) | `Temporal_Workflow_Id__c` (Text, External ID, Unique), `Driver_Id__c` |

   WorkOrder is available in orgs with Service Cloud or Field Service. If yours does not have it, use Case or a custom object and change the object name in `UpsertWorkOrderAsync`.

4. **Configure the workers** (the API and the mocks do not need Salesforce settings):

   ```bash
   export Salesforce__LoginUrl=https://yourco--fleetdemo.sandbox.my.salesforce.com
   export Salesforce__ClientId=<consumer key>
   export Salesforce__ClientSecret=<consumer secret>
   export Salesforce__ApiVersion=v62.0
   ```

   On Windows use `$env:Salesforce__LoginUrl = '...'` before `scripts\start-all.ps1`, or edit `src/Fleet.Worker/appsettings.json`.

5. **Seed the sandbox** with a few Equipment__c and Driver__c records whose unit numbers match the simulator (41201 to 41224, drivers D2001 to D2024), or change `FleetSeed.cs` to your units.

6. **Change events (optional).** In production, subscribe to Change Data Capture for Driver__c (and Equipment__c) through the Pub/Sub API and either call `POST /api/integrations/salesforce/events` or start the driver workflow directly with the Temporal client. A record-triggered Flow with an HTTP callout also works for a demo. Either way the scheduled `EquipmentSyncWorkflow` catches anything missed, and duplicate events are harmless because workflow IDs are business keys.

## Checking it

- Run the demo, inject a fault on a truck, and watch the WorkOrder appear in the sandbox with `Temporal_Workflow_Id__c` set to the case's workflow ID.
- Temporarily revoke the integration user's API permission: the `UpsertWorkOrder` activity fails fast with Salesforce's message, visible in the Temporal UI. Restore it and use the Temporal UI or CLI to reset the workflow to before that step, or let a retryable error recover on its own.
