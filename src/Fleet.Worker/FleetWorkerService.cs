using System.Net.Http.Json;
using Fleet.Core;
using Fleet.Integrations;
using Fleet.Workflows;
using Fleet.Workflows.Activities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Worker;

namespace Fleet.Worker;

/// <summary>
/// Hosts one Temporal worker per selected task queue. Workers only make outbound connections:
/// to Temporal (long-poll for tasks) and to the systems their activities call. Nothing calls into them,
/// which is why they can run inside a private network next to internal APIs.
/// </summary>
public class FleetWorkerService : BackgroundService
{
    private readonly IConfiguration config;
    private readonly ILogger<FleetWorkerService> log;

    public FleetWorkerService(IConfiguration config, ILogger<FleetWorkerService> log)
    {
        this.config = config;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var temporal = config.GetSection("Temporal").Get<TemporalSettings>() ?? new TemporalSettings();
        var endpoints = config.GetSection("Endpoints").Get<EndpointSettings>() ?? new EndpointSettings();
        var salesforceOptions = config.GetSection("Salesforce").Get<SalesforceOptions>() ?? new SalesforceOptions();
        var advisorOptions = config.GetSection("Advisor").Get<AdvisorOptions>() ?? new AdvisorOptions();
        var demo = new DemoSettings
        {
            TimeScale = config.GetValue("Demo:TimeScale", DemoSettings.DefaultTimeScale),
            ApprovalThreshold = config.GetValue("Demo:ApprovalThreshold", 2500m),
        };
        var queues = ParseQueues(config["queues"]);

        log.LogInformation("Connecting to Temporal at {Address} (namespace {Namespace})", temporal.Address, temporal.Namespace);
        var client = await TemporalConnector.ConnectWithRetryAsync(temporal, m => log.LogWarning("{Message}", m), stoppingToken);

        var externalSystems = new HttpClient { BaseAddress = new Uri(endpoints.ExternalSystems), Timeout = TimeSpan.FromSeconds(15) };
        var telematics = new TelematicsClient(externalSystems);
        var tms = new TmsClient(externalSystems);
        var vendors = new VendorPortalClient(externalSystems);
        var ivr = new IvrClient(externalSystems);
        var kafka = new KafkaClient(externalSystems);
        var salesforce = new SalesforceRestClient(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, salesforceOptions);
        var advisor = new ClaudeAdvisor(new HttpClient { Timeout = TimeSpan.FromSeconds(40) }, advisorOptions);
        var entityActivities = new EntityActivities(client);

        var workers = new List<TemporalWorker>();
        foreach (var queue in queues)
        {
            var options = new TemporalWorkerOptions(queue);

            switch (queue)
            {
                case TaskQueues.Entities:
                    options.AddWorkflow<TruckWorkflow>()
                        .AddWorkflow<DriverWorkflow>()
                        .AddWorkflow<EquipmentSyncWorkflow>()
                        .AddAllActivities(entityActivities)
                        .AddAllActivities(new SyncActivities(client, demo));
                    break;
                case TaskQueues.Dispatch:
                    options.AddWorkflow<LoadWorkflow>()
                        .AddAllActivities(entityActivities)
                        .AddAllActivities(new DispatchActivities(client, telematics, kafka));
                    break;
                case TaskQueues.Maintenance:
                    options.AddWorkflow<MaintenanceWorkflow>()
                        .AddWorkflow<SafetyCoachingWorkflow>()
                        .AddAllActivities(entityActivities)
                        .AddAllActivities(new MaintenanceActivities(telematics, vendors, advisor))
                        .AddAllActivities(new SafetyActivities(ivr));
                    break;
                case TaskQueues.Integrations:
                    options.AddAllActivities(new SalesforceActivities(salesforce))
                        .AddAllActivities(new TmsActivities(tms));
                    break;
            }

            workers.Add(new TemporalWorker(client, options));
            log.LogInformation("Polling task queue {Queue}", queue);
        }

        if (advisor.IsConfigured)
        {
            log.LogInformation("Maintenance advisor: Claude ({Model})", advisorOptions.Model);
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatLoopAsync(endpoints.Api, queues, heartbeatCts.Token);

        try
        {
            await Task.WhenAll(workers.Select(w => w.ExecuteAsync(stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown (Ctrl+C).
        }
        finally
        {
            heartbeatCts.Cancel();
            await SayGoodbyeAsync(endpoints.Api, queues);
            foreach (var worker in workers)
            {
                worker.Dispose();
            }

            log.LogInformation("Workers stopped. Running workflows are safe on the server and resume when a worker returns.");
        }

        try
        {
            await heartbeat;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static List<string> ParseQueues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return TaskQueues.All.ToList();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(q => q.StartsWith("fleet-", StringComparison.Ordinal) ? q : $"fleet-{q}")
            .Where(q => TaskQueues.All.Contains(q))
            .Distinct()
            .ToList();
    }

    /// <summary>Lets the fleet UI show which worker processes are alive (for the "kill a worker" scenario).</summary>
    private async Task HeartbeatLoopAsync(string apiBase, List<string> queues, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(2) };
        var body = new
        {
            identity = $"{Environment.MachineName}:{Environment.ProcessId}",
            queues,
            pid = Environment.ProcessId,
            startedAt = DateTime.UtcNow,
        };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await http.PostAsJsonAsync("/api/workers/heartbeat", body, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The API may not be running. Heartbeats are best effort.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task SayGoodbyeAsync(string apiBase, List<string> queues)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(1) };
            await http.PostAsJsonAsync("/api/workers/goodbye", new { identity = $"{Environment.MachineName}:{Environment.ProcessId}", queues });
        }
        catch (Exception)
        {
        }
    }
}
