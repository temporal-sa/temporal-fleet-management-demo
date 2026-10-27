using System.Globalization;
using System.Text.Json.Nodes;
using Fleet.Core;
using Fleet.Integrations;
using Temporalio.Activities;

namespace Fleet.Workflows.Activities;

/// <summary>
/// Salesforce integration. Runs on the fleet-integrations task queue so it can be scaled, rate limited
/// or taken offline independently of the business workflows.
///
/// Every write is an upsert keyed by an external ID (the Temporal workflow ID). If an attempt times out
/// after Salesforce committed the change, the retry updates the same record instead of creating a second one.
/// </summary>
public class SalesforceActivities
{
    private readonly SalesforceRestClient salesforce;

    public SalesforceActivities(SalesforceRestClient salesforce) => this.salesforce = salesforce;

    [Activity]
    public async Task<WorkOrderRef> UpsertWorkOrderAsync(WorkOrderUpsert workOrder)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var fields = Compact(new Dictionary<string, object?>
        {
            ["Subject"] = workOrder.Subject,
            ["Description"] = workOrder.Description,
            ["Priority"] = workOrder.Priority,
            ["Status"] = workOrder.Status,
            ["Unit_Number__c"] = workOrder.Unit,
            ["Shop_Name__c"] = workOrder.ShopName,
            ["Estimated_Cost__c"] = workOrder.EstimatedCost,
            ["Actual_Cost__c"] = workOrder.ActualCost,
        });

        var result = await ExternalCall.RunAsync(() =>
            salesforce.UpsertByExternalIdAsync("WorkOrder", "Temporal_Workflow_Id__c", workOrder.ExternalId, fields, ct));
        var record = await ExternalCall.RunAsync(() => salesforce.GetRecordAsync("WorkOrder", result.Id, ct));
        return new WorkOrderRef(result.Id, record?["WorkOrderNumber"]?.GetValue<string>(), result.Created);
    }

    [Activity]
    public async Task<string> UpsertSafetyCaseAsync(SafetyCaseUpsert safetyCase)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var fields = Compact(new Dictionary<string, object?>
        {
            ["Subject"] = safetyCase.Subject,
            ["Description"] = safetyCase.Description,
            ["Status"] = safetyCase.Status,
            ["Priority"] = safetyCase.Priority,
            ["Origin"] = "Telematics",
            ["Driver_Id__c"] = safetyCase.DriverId,
        });
        var result = await ExternalCall.RunAsync(() =>
            salesforce.UpsertByExternalIdAsync("Case", "Temporal_Workflow_Id__c", safetyCase.ExternalId, fields, ct));
        return result.Id;
    }

    [Activity]
    public async Task UpdateDriverOnboardingAsync(DriverOnboardingUpdate update)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var fields = Compact(new Dictionary<string, object?>
        {
            ["Hire_Status__c"] = update.HireStatus,
            ["Onboarding_Step__c"] = update.CurrentStep,
            ["Onboarding_Progress__c"] = $"{update.StepsComplete}/{update.StepsTotal}",
        });
        await ExternalCall.RunAsync(() =>
            salesforce.UpsertByExternalIdAsync("Driver__c", "Driver_Id__c", update.DriverId, fields, ct));
    }

    /// <summary>Reads the fleet master data (equipment and drivers) with SOQL.</summary>
    [Activity]
    public async Task<SalesforceFleetSnapshot> FetchFleetAsync()
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var equipment = await ExternalCall.RunAsync(() => salesforce.QueryAsync(
            "SELECT Id, Name, Unit_Number__c, Home_Terminal__c, Make__c, Model__c, Year__c, VIN__c, Odometer__c, PM_Due_Odometer__c " +
            "FROM Equipment__c WHERE Status__c = 'Active'", ct));
        var drivers = await ExternalCall.RunAsync(() => salesforce.QueryAsync(
            "SELECT Id, Name, Driver_Id__c, Home_Terminal__c, Hire_Status__c, Endorsements__c, Phone__c, Paired_Unit__c " +
            "FROM Driver__c WHERE Hire_Status__c != 'Terminated'", ct));

        return new SalesforceFleetSnapshot(
            equipment.Select(r => new SalesforceEquipment(
                Str(r, "Id"),
                Str(r, "Unit_Number__c"),
                Str(r, "Home_Terminal__c"),
                Str(r, "Make__c"),
                Str(r, "Model__c"),
                Int(r, "Year__c"),
                Str(r, "VIN__c"),
                Int(r, "Odometer__c"),
                Int(r, "PM_Due_Odometer__c"))).ToList(),
            drivers.Select(r => new SalesforceDriver(
                Str(r, "Id"),
                Str(r, "Driver_Id__c"),
                Str(r, "Name"),
                Str(r, "Home_Terminal__c"),
                Str(r, "Hire_Status__c"),
                Str(r, "Endorsements__c").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                Str(r, "Phone__c"),
                NullIfEmpty(Str(r, "Paired_Unit__c")))).ToList());
    }

    private static Dictionary<string, object?> Compact(Dictionary<string, object?> fields) =>
        fields.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static string Str(JsonObject record, string field) =>
        record[field] is JsonValue v ? v.ToString() : "";

    private static int Int(JsonObject record, string field) =>
        record[field] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? (int)d
            : 0;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
