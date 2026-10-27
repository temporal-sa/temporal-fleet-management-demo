using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fleet.Api.Services;
using Fleet.Core;
using Fleet.Workflows;
using Temporalio.Client;
using Temporalio.Exceptions;

// Fleet.Api: backend for the fleet UI. Default URL http://localhost:5100.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton(new DemoControls { AutoTender = builder.Configuration.GetValue("Demo:AutoTender", true) });
builder.Services.AddSingleton<TemporalClientProvider>();
builder.Services.AddSingleton<ExternalSystemsProxy>();
builder.Services.AddSingleton<WorkerRegistry>();
builder.Services.AddSingleton<FleetStateCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetStateCache>());
builder.Services.AddSingleton<LoadTenderer>();
builder.Services.AddSingleton<ScenarioRunner>();
builder.Services.AddHostedService<Bootstrapper>();
builder.Services.AddHostedService<AutoTenderService>();

var app = builder.Build();
app.UseCors();

// ---------------------------------------------------------------- read models

app.MapGet("/api/snapshot", (FleetStateCache cache) => Results.Json(cache.Current));

app.MapGet("/api/network", () => Results.Json(new
{
    hubs = RoadNetwork.Hubs,
    corridors = RoadNetwork.Corridors,
    shops = FleetSeed.Shops.Select(s => new
    {
        s.ShopId,
        s.Name,
        s.HubKey,
        s.IsPartner,
        capabilities = s.Capabilities.Select(c => c.ToString()),
        lat = RoadNetwork.Get(s.HubKey).Lat,
        lon = RoadNetwork.Get(s.HubKey).Lon,
    }),
}));

app.MapGet("/api/events", async (long? after, ExternalSystemsProxy external) =>
    Results.Json(await external.GetNodeAsync($"/events?after={after ?? 0}&limit=150") ?? new JsonArray()));

app.MapGet("/api/integrations", async (ExternalSystemsProxy external) =>
{
    var tasks = new Dictionary<string, Task<JsonNode?>>
    {
        ["salesforceCalls"] = external.GetNodeAsync("/admin/salesforce/calls"),
        ["workOrders"] = external.GetNodeAsync("/admin/salesforce/objects/WorkOrder"),
        ["cases"] = external.GetNodeAsync("/admin/salesforce/objects/Case"),
        ["salesforceDrivers"] = external.GetNodeAsync("/admin/salesforce/objects/Driver__c"),
        ["tmsCalls"] = external.GetNodeAsync("/tms/admin/calls"),
        ["tmsEquipment"] = external.GetNodeAsync("/tms/admin/equipment"),
        ["tmsLoads"] = external.GetNodeAsync("/tms/api/loads"),
        ["kafka"] = external.GetNodeAsync("/kafka/topics"),
        ["vendors"] = external.GetNodeAsync("/vendors/requests"),
        ["ivr"] = external.GetNodeAsync("/ivr/calls"),
    };
    await Task.WhenAll(tasks.Values);
    var result = new JsonObject();
    foreach (var (key, task) in tasks)
    {
        result[key] = task.Result?.DeepClone();
    }

    return Results.Json(result);
});

// Pending activities for one workflow: shows "attempt 9, last failure: TMS returned 503" during an outage.
app.MapGet("/api/workflows/{workflowId}/pending", async (string workflowId, TemporalClientProvider temporal) =>
{
    var client = await temporal.GetAsync();
    try
    {
        // Raw DescribeWorkflowExecution call (same pattern as Connection.OperatorService in Bootstrapper).
        // It includes pending activities with attempt counts and the last failure message.
        var raw = await client.Connection.WorkflowService.DescribeWorkflowExecutionAsync(
            new Temporalio.Api.WorkflowService.V1.DescribeWorkflowExecutionRequest
            {
                Namespace = temporal.Settings.Namespace,
                Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = workflowId },
            });
        var info = raw.WorkflowExecutionInfo;
        return Results.Json(new
        {
            workflowId,
            runId = info?.Execution?.RunId,
            status = info?.Status.ToString(),
            historyLength = info?.HistoryLength ?? 0,
            pendingActivities = raw.PendingActivities.Select(p => new
            {
                activity = p.ActivityType?.Name,
                attempt = p.Attempt,
                state = p.State.ToString(),
                lastFailure = p.LastFailure?.Message,
            }),
        });
    }
    catch (Exception e)
    {
        return Results.NotFound(new { message = e.Message });
    }
});

// ---------------------------------------------------------------- commands

app.MapPost("/api/loads/tender", async (TenderRequest? request, LoadTenderer tenderer) =>
{
    var input = await tenderer.TenderAsync(request ?? new TenderRequest(null, null, null, null));
    return Results.Json(new { input.LoadId, workflowId = WorkflowIds.Load(input.LoadId) });
});

app.MapPost("/api/loads/{loadId}/nudge", async (string loadId, TemporalClientProvider temporal) =>
{
    var client = await temporal.GetAsync();
    await client.GetWorkflowHandle<LoadWorkflow>(WorkflowIds.Load(loadId)).SignalAsync(wf => wf.CapacityChangedAsync());
    return Results.Ok();
});

// Human-in-the-loop approval. A Workflow Update is validated by the workflow itself and answers synchronously,
// so a double click or a stale screen gets a clear rejection instead of a second decision.
app.MapPost("/api/repairs/{maintenanceId}/decision", async (string maintenanceId, ApprovalDecision decision, TemporalClientProvider temporal, ExternalSystemsProxy external) =>
{
    var client = await temporal.GetAsync();
    try
    {
        var result = await client.GetWorkflowHandle<MaintenanceWorkflow>(maintenanceId).ExecuteUpdateAsync(wf => wf.DecideAsync(decision));
        await external.LogAsync("Fleet UI", "decision",
            $"{decision.DecidedBy} {(decision.Approve ? "approved" : "rejected")} {maintenanceId}{(decision.AlternateShopId != null ? $" (shop {decision.AlternateShopId})" : "")}.",
            maintenanceId);
        return Results.Json(new { result });
    }
    catch (WorkflowUpdateFailedException e)
    {
        return Results.Conflict(new { message = e.InnerException?.Message ?? e.Message });
    }
});

app.MapPost("/api/repairs/{maintenanceId}/cancel", async (string maintenanceId, CancelRequest request, TemporalClientProvider temporal) =>
{
    var client = await temporal.GetAsync();
    try
    {
        var result = await client.GetWorkflowHandle<MaintenanceWorkflow>(maintenanceId).ExecuteUpdateAsync(wf => wf.CancelCaseAsync(request.Reason ?? "cancelled from the fleet UI"));
        return Results.Json(new { result });
    }
    catch (WorkflowUpdateFailedException e)
    {
        return Results.Conflict(new { message = e.InnerException?.Message ?? e.Message });
    }
});

app.MapPost("/api/coaching/{coachingId}/resolve", async (string coachingId, CoachingResolution resolution, TemporalClientProvider temporal) =>
{
    var client = await temporal.GetAsync();
    try
    {
        var result = await client.GetWorkflowHandle<SafetyCoachingWorkflow>(coachingId).ExecuteUpdateAsync(wf => wf.ResolveAsync(resolution));
        return Results.Json(new { result });
    }
    catch (WorkflowUpdateFailedException e)
    {
        return Results.Conflict(new { message = e.InnerException?.Message ?? e.Message });
    }
});

app.MapPost("/api/vendors/{requestId}/{action}", async (string requestId, string action, VendorAction? body, ExternalSystemsProxy external) =>
{
    var path = action switch
    {
        "accept" or "decline" => $"/vendors/requests/{requestId}/respond",
        "complete" => $"/vendors/requests/{requestId}/complete",
        "resend-completion" => $"/vendors/requests/{requestId}/resend-completion",
        _ => null,
    };
    if (path == null)
    {
        return Results.BadRequest(new { message = $"Unknown action {action}" });
    }

    object? payload = action is "accept" or "decline" ? new { accepted = action == "accept", note = body?.Note } : null;
    var (status, response) = await external.PostAsync(path, payload);
    return Results.Json(response, statusCode: status);
});

app.MapPost("/api/ivr/{callId}/acknowledge", async (string callId, ExternalSystemsProxy external) =>
{
    var (status, response) = await external.PostAsync($"/ivr/calls/{callId}/acknowledge");
    return Results.Json(response, statusCode: status);
});

app.MapPost("/api/scenarios/{name}", async (string name, ScenarioRequest? request, ScenarioRunner runner, ExternalSystemsProxy external) =>
{
    var result = await runner.RunAsync(name, request ?? new ScenarioRequest(null, null));
    if (result.Ok)
    {
        await external.LogAsync("Demo", "scenario", result.Message, result.WorkflowId);
    }

    return Results.Json(result, statusCode: result.Ok ? 200 : 409);
});

app.MapGet("/api/chaos", async (ExternalSystemsProxy external) => Results.Json(await external.GetNodeAsync("/chaos")));

app.MapPut("/api/chaos", async (JsonObject patch, ExternalSystemsProxy external) =>
{
    var (status, body) = await external.SendAsync(HttpMethod.Put, "/chaos", patch);
    return Results.Json(body, statusCode: status);
});

app.MapPut("/api/controls", (ControlsPatch patch, DemoControls controls) =>
{
    if (patch.AutoTender.HasValue)
    {
        controls.AutoTender = patch.AutoTender.Value;
    }

    return Results.Json(controls);
});

app.MapPost("/api/sync/run", async (TemporalClientProvider temporal) =>
{
    var client = await temporal.GetAsync();
    await client.GetScheduleHandle(WorkflowIds.EquipmentSyncSchedule).TriggerAsync();
    return Results.Ok();
});

// ---------------------------------------------------------------- worker presence

app.MapPost("/api/workers/heartbeat", (WorkerHeartbeat beat, WorkerRegistry registry) =>
{
    registry.Heartbeat(beat);
    return Results.Ok();
});

app.MapPost("/api/workers/goodbye", (WorkerGoodbye body, WorkerRegistry registry) =>
{
    registry.Goodbye(body.Identity);
    return Results.Ok();
});

// ---------------------------------------------------------------- Salesforce Change Data Capture webhook

// In production this is a Pub/Sub API subscriber. A new Driver__c record starts the driver's entity workflow;
// the workflow ID is the driver ID, so a duplicate event or the scheduled sync can never start a second one.
app.MapPost("/api/integrations/salesforce/events", async (JsonObject evt, TemporalClientProvider temporal, ExternalSystemsProxy external, IConfiguration config) =>
{
    var header = evt["ChangeEventHeader"];
    var entity = header?["entityName"]?.ToString();
    var changeType = header?["changeType"]?.ToString();
    if (entity != "Driver__c" || changeType != "CREATE")
    {
        return Results.Ok(new { ignored = true });
    }

    string Text(string field) => evt[field]?.ToString() ?? "";
    var driverId = Text("Driver_Id__c");
    var input = new DriverInput
    {
        DriverId = driverId,
        Name = Text("Name"),
        HomeHub = string.IsNullOrEmpty(Text("Home_Terminal__c")) ? "OMA" : Text("Home_Terminal__c"),
        Endorsements = Text("Endorsements__c").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList(),
        Employment = Text("Hire_Status__c") == "Active" ? DriverEmployment.Active : DriverEmployment.Onboarding,
        SalesforceId = Text("Id"),
        Phone = Text("Phone__c"),
        Settings = new DemoSettings
        {
            TimeScale = config.GetValue("Demo:TimeScale", DemoSettings.DefaultTimeScale),
            ApprovalThreshold = config.GetValue("Demo:ApprovalThreshold", 2500m),
        },
    };

    var client = await temporal.GetAsync();
    try
    {
        await client.StartWorkflowAsync(
            (DriverWorkflow wf) => wf.RunAsync(input),
            new(id: WorkflowIds.Driver(driverId), taskQueue: TaskQueues.Entities));
        await external.LogAsync("Fleet API", "signal", $"Salesforce change event for {input.Name}: started {WorkflowIds.Driver(driverId)} (onboarding).", WorkflowIds.Driver(driverId));
        return Results.Ok(new { started = true });
    }
    catch (WorkflowAlreadyStartedException)
    {
        return Results.Ok(new { started = false, reason = "already running" });
    }
});

app.MapGet("/api/health", (FleetStateCache cache) => Results.Json(new
{
    temporal = cache.Current.TemporalConnected,
    externalSystems = cache.Current.ExternalSystemsConnected,
}));

app.Run();

public record CancelRequest(string? Reason);

public record VendorAction(string? Note);

public record ControlsPatch(bool? AutoTender);

public record WorkerGoodbye(string Identity);
