using System.Text.Json.Serialization;
using Fleet.ExternalSystems.Infrastructure;
using Fleet.ExternalSystems.Tms;
using Fleet.ExternalSystems.Misc;
using Fleet.ExternalSystems.Salesforce;
using Fleet.ExternalSystems.Telematics;
using Fleet.ExternalSystems.Vendors;

// Fleet.ExternalSystems: mocks of the systems a fleet operator typically already has. Default URL http://localhost:5050.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient();
builder.Services.AddSingleton(new ChaosState { TimeScale = builder.Configuration.GetValue("Demo:TimeScale", 180) });
builder.Services.AddSingleton<EventLog>();
builder.Services.AddSingleton<TemporalBridge>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TemporalBridge>());
builder.Services.AddSingleton<TelematicsSimulator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelematicsSimulator>());
builder.Services.AddSingleton<SalesforceStore>();
builder.Services.AddSingleton<TmsStore>();
builder.Services.AddSingleton<KafkaStore>();
builder.Services.AddSingleton<IvrService>();
builder.Services.AddSingleton<VendorPortal>();

var app = builder.Build();

app.MapGet("/health", (TemporalBridge bridge) => Results.Json(new { status = "ok", temporalConnected = bridge.Connected }));

app.MapGet("/chaos", (ChaosState chaos) => Results.Json(chaos));
app.MapPut("/chaos", (ChaosPatch patch, ChaosState chaos, EventLog log) =>
{
    foreach (var change in patch.ApplyTo(chaos))
    {
        log.Add("Demo", "chaos", change);
    }

    return Results.Json(chaos);
});

app.MapGet("/events", (long? after, int? limit, EventLog log) => Results.Json(log.Since(after ?? 0, limit ?? 150)));
app.MapPost("/events", (EventPost body, EventLog log) =>
{
    log.Add(body.System, body.Kind, body.Message, body.WorkflowId);
    return Results.Ok();
});

app.MapTelematics();
app.MapSalesforce();
app.MapTms();
app.MapVendors();
app.MapKafkaAndIvr();

app.Logger.LogInformation("External systems (Salesforce, TMS, telematics, vendors, IVR, Kafka) listening");
app.Run();

public record EventPost(string System, string Kind, string Message, string? WorkflowId);
