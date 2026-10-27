using Fleet.Core;
using Fleet.Workflows;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using IndexedValueType = Temporalio.Api.Enums.V1.IndexedValueType;

namespace Fleet.Api.Services;

/// <summary>
/// One-time setup on API start:
///  1. Register custom search attributes (local dev server only; on Temporal Cloud create them in the UI or tcld).
///  2. Create the equipment-sync Schedule and trigger it once. That run reads Salesforce and starts
///     every truck and driver entity workflow.
///  3. Tender a few loads so the map has freight on it.
/// </summary>
public class Bootstrapper : BackgroundService
{
    private readonly TemporalClientProvider temporal;
    private readonly LoadTenderer tenderer;
    private readonly ExternalSystemsProxy external;
    private readonly IConfiguration config;
    private readonly ILogger<Bootstrapper> log;

    public Bootstrapper(TemporalClientProvider temporal, LoadTenderer tenderer, ExternalSystemsProxy external, IConfiguration config, ILogger<Bootstrapper> log)
    {
        this.temporal = temporal;
        this.tenderer = tenderer;
        this.external = external;
        this.config = config;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = await temporal.GetAsync();
        if (temporal.Settings.IsLocalDevServer)
        {
            await EnsureSearchAttributesAsync(client);
        }

        await EnsureScheduleAsync(client);

        // Wait for the entity workflows the schedule starts.
        var running = 0;
        for (var i = 0; i < 90 && !stoppingToken.IsCancellationRequested; i++)
        {
            running = 0;
            await foreach (var _ in client.ListWorkflowsAsync("WorkflowType = 'TruckWorkflow' AND ExecutionStatus = 'Running'"))
            {
                running++;
            }

            if (running >= FleetSeed.Trucks.Count)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }

        log.LogInformation("{Count} truck entity workflows running", running);
        await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);

        var activeLoads = 0;
        await foreach (var _ in client.ListWorkflowsAsync("WorkflowType = 'LoadWorkflow' AND ExecutionStatus = 'Running'"))
        {
            activeLoads++;
        }

        var initial = config.GetValue("Demo:InitialLoads", 9);
        for (var i = activeLoads; i < initial && !stoppingToken.IsCancellationRequested; i++)
        {
            try
            {
                await tenderer.TenderAsync(new TenderRequest(null, null, null, null));
            }
            catch (Exception e)
            {
                log.LogWarning("Initial tender failed: {Message}", e.Message);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1200), stoppingToken);
        }
    }

    private async Task EnsureSearchAttributesAsync(TemporalClient client)
    {
        try
        {
            var ns = temporal.Settings.Namespace;
            var existing = await client.Connection.OperatorService.ListSearchAttributesAsync(
                new ListSearchAttributesRequest { Namespace = ns });
            var request = new AddSearchAttributesRequest { Namespace = ns };
            foreach (var (name, type) in FleetSearchAttributes.Definitions)
            {
                if (!existing.CustomAttributes.ContainsKey(name))
                {
                    request.SearchAttributes.Add(name, type == "Bool" ? IndexedValueType.Bool : IndexedValueType.Keyword);
                }
            }

            if (request.SearchAttributes.Count > 0)
            {
                await client.Connection.OperatorService.AddSearchAttributesAsync(request);
                log.LogInformation("Registered search attributes: {Names}", string.Join(", ", request.SearchAttributes.Keys));
            }
        }
        catch (Exception e)
        {
            log.LogWarning("Could not register search attributes ({Message}). Start the dev server with scripts/start-temporal.*", e.Message);
        }
    }

    private async Task EnsureScheduleAsync(TemporalClient client)
    {
        try
        {
            await client.CreateScheduleAsync(
                WorkflowIds.EquipmentSyncSchedule,
                new Schedule(
                    Action: ScheduleActionStartWorkflow.Create(
                        (EquipmentSyncWorkflow wf) => wf.RunAsync(),
                        new(id: WorkflowIds.EquipmentSyncWorkflowPrefix, taskQueue: TaskQueues.Entities)),
                    Spec: new ScheduleSpec
                    {
                        Intervals = new List<ScheduleIntervalSpec> { new(Every: TimeSpan.FromSeconds(60)) },
                    }));
            log.LogInformation("Created schedule {Id}", WorkflowIds.EquipmentSyncSchedule);
        }
        catch (Exception e)
        {
            log.LogInformation("Schedule {Id} not created ({Message}); it probably exists already", WorkflowIds.EquipmentSyncSchedule, e.Message);
        }

        try
        {
            await client.GetScheduleHandle(WorkflowIds.EquipmentSyncSchedule).TriggerAsync();
            await external.LogAsync("Fleet API", "info", "Equipment sync triggered. It reads Salesforce and starts the truck and driver entity workflows.");
        }
        catch (Exception e)
        {
            log.LogWarning("Could not trigger the sync schedule: {Message}", e.Message);
        }
    }
}
