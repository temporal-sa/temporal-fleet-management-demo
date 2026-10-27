using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Fleet.Core;
using Fleet.Workflows;
using WorkflowExecutionStatus = Temporalio.Api.Enums.V1.WorkflowExecutionStatus;
using Temporalio.Client;

namespace Fleet.Api.Services;

public class EntityView<T>
    where T : class
{
    public string WorkflowId { get; set; } = "";
    public string? RunId { get; set; }
    public string ExecutionStatus { get; set; } = "Running";
    public DateTime StartTime { get; set; }
    public DateTime? CloseTime { get; set; }
    public bool Stale { get; set; }
    public T? State { get; set; }
}

public class TruckView
{
    public string Unit { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public string? RunId { get; set; }
    public bool Stale { get; set; }
    public TelematicsTruckDto? Telematics { get; set; }
    public TruckState? Entity { get; set; }
    public TmsEquipmentStatus? Tms { get; set; }
    public string? DriverName { get; set; }
}

public class DriverView : EntityView<DriverState>
{
    public TelematicsDriverDto? Eld { get; set; }
}

public class InboxItem
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public string? RunId { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime Since { get; set; }
}

public class Kpis
{
    public int TrucksAvailable { get; set; }
    public int TrucksOnLoad { get; set; }
    public int TrucksInShop { get; set; }
    public int TrucksDown { get; set; }
    public int ActiveLoads { get; set; }
    public int DeliveredLoads { get; set; }
    public int Repowers { get; set; }
    public int OpenRepairs { get; set; }
    public int CompletedRepairs { get; set; }
    public int PartnerRepairs { get; set; }
    public decimal SavingsCaptured { get; set; }
    public int PendingApprovals { get; set; }
    public int DuplicatesAbsorbed { get; set; }
    public int DriversOnboarding { get; set; }
    public int SafetyCallsHeld { get; set; }
}

public class SyncView
{
    public string WorkflowId { get; set; } = "";
    public string? RunId { get; set; }
    public SyncReport? Report { get; set; }
}

public class FleetSnapshot
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SimTime { get; set; }
    public int TimeScale { get; set; } = DemoSettings.DefaultTimeScale;
    public bool TemporalConnected { get; set; }
    public bool ExternalSystemsConnected { get; set; }
    public string TemporalUi { get; set; } = "";
    public string Namespace { get; set; } = "default";
    public bool AutoTender { get; set; }
    public Kpis Kpis { get; set; } = new();
    public List<TruckView> Trucks { get; set; } = new();
    public List<DriverView> Drivers { get; set; } = new();
    public List<EntityView<LoadState>> Loads { get; set; } = new();
    public List<EntityView<MaintenanceState>> Repairs { get; set; } = new();
    public List<EntityView<SafetyCoachingState>> Coaching { get; set; } = new();
    public List<InboxItem> Inbox { get; set; } = new();
    public List<WorkerQueueView> Workers { get; set; } = new();
    public JsonNode? Chaos { get; set; }
    public SyncView? Sync { get; set; }
}

public class DemoControls
{
    public bool AutoTender { get; set; }
}

/// <summary>
/// Builds the snapshot the UI polls. Everything about trucks, drivers, loads and repairs comes from
/// Temporal: visibility queries list the workflows, workflow queries read their live state.
/// There is no separate application database in this demo.
/// </summary>
public class FleetStateCache : BackgroundService
{
    private readonly TemporalClientProvider temporal;
    private readonly ExternalSystemsProxy external;
    private readonly WorkerRegistry workers;
    private readonly DemoControls controls;
    private readonly ILogger<FleetStateCache> log;

    private readonly Dictionary<string, EntityView<TruckState>> trucks = new();
    private readonly Dictionary<string, DriverView> drivers = new();
    private readonly Dictionary<string, EntityView<LoadState>> loads = new();
    private readonly Dictionary<string, EntityView<MaintenanceState>> repairs = new();
    private readonly Dictionary<string, EntityView<SafetyCoachingState>> coaching = new();
    private SyncView? sync;

    public FleetStateCache(
        TemporalClientProvider temporal, ExternalSystemsProxy external, WorkerRegistry workers, DemoControls controls, ILogger<FleetStateCache> log)
    {
        this.temporal = temporal;
        this.external = external;
        this.workers = workers;
        this.controls = controls;
        this.log = log;
    }

    public FleetSnapshot Current { get; private set; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = temporal.GetAsync();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync();
            }
            catch (Exception e)
            {
                log.LogWarning("Snapshot refresh failed: {Message}", e.Message);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1500), stoppingToken);
        }
    }

    private async Task RefreshAsync()
    {
        var fleetTask = external.GetFleetAsync();
        var chaosTask = external.GetNodeAsync("/chaos");
        var tmsTask = external.GetAsync<List<TmsEquipmentStatus>>("/tms/admin/equipment");
        var client = temporal.Client;

        if (client != null)
        {
            var since = DateTime.UtcNow.AddHours(-3).ToString("yyyy-MM-ddTHH:mm:ssZ");
            await Task.WhenAll(
                RefreshEntitiesAsync<TruckWorkflow, TruckState>(client, "WorkflowType = 'TruckWorkflow' AND ExecutionStatus = 'Running'", trucks, wf => wf.GetState(), 200),
                RefreshDriversAsync(client),
                RefreshEntitiesAsync<LoadWorkflow, LoadState>(client, $"WorkflowType = 'LoadWorkflow' AND StartTime > '{since}'", loads, wf => wf.GetState(), 80),
                RefreshEntitiesAsync<MaintenanceWorkflow, MaintenanceState>(client, $"WorkflowType = 'MaintenanceWorkflow' AND StartTime > '{since}'", repairs, wf => wf.GetState(), 60),
                RefreshEntitiesAsync<SafetyCoachingWorkflow, SafetyCoachingState>(client, $"WorkflowType = 'SafetyCoachingWorkflow' AND StartTime > '{since}'", coaching, wf => wf.GetState(), 40),
                RefreshSyncAsync(client));
        }

        var fleet = await fleetTask;
        var chaos = await chaosTask;
        var tms = (await tmsTask ?? new List<TmsEquipmentStatus>()).ToDictionary(m => m.Unit);
        Current = Build(fleet, chaos, tms, client != null);
    }

    private async Task RefreshEntitiesAsync<TWorkflow, TState>(
        TemporalClient client,
        string query,
        Dictionary<string, EntityView<TState>> cache,
        Expression<Func<TWorkflow, TState>> stateQuery,
        int limit)
        where TState : class
    {
        var executions = new List<WorkflowExecution>();
        await foreach (var wf in client.ListWorkflowsAsync(query))
        {
            executions.Add(wf);
            if (executions.Count >= limit)
            {
                break;
            }
        }

        var seen = new HashSet<string>();
        await Task.WhenAll(executions.Select(async exec =>
        {
            lock (seen)
            {
                if (!seen.Add(exec.Id))
                {
                    return;
                }
            }

            EntityView<TState>? view;
            lock (cache)
            {
                cache.TryGetValue(exec.Id, out view);
            }

            var status = Describe(exec.Status);
            var needsQuery = exec.Status == WorkflowExecutionStatus.Running || view?.State == null || view.ExecutionStatus == "Running";
            TState? state = view?.State;
            var stale = false;
            if (needsQuery)
            {
                var fresh = await TryQueryAsync(client, exec.Id, stateQuery);
                if (fresh != null)
                {
                    state = fresh;
                }
                else
                {
                    stale = exec.Status == WorkflowExecutionStatus.Running;
                }
            }

            var updated = new EntityView<TState>
            {
                WorkflowId = exec.Id,
                RunId = exec.RunId,
                ExecutionStatus = status,
                StartTime = exec.StartTime,
                CloseTime = exec.CloseTime,
                Stale = stale,
                State = state,
            };
            lock (cache)
            {
                cache[exec.Id] = updated;
            }
        }));

        // Entities that stopped running (terminated during a reset) drop out of running-only lists.
        if (query.Contains("ExecutionStatus = 'Running'", StringComparison.Ordinal))
        {
            lock (cache)
            {
                foreach (var gone in cache.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    cache.Remove(gone);
                }
            }
        }
    }

    private async Task RefreshDriversAsync(TemporalClient client)
    {
        var temp = new Dictionary<string, EntityView<DriverState>>();
        lock (drivers)
        {
            foreach (var (k, v) in drivers)
            {
                temp[k] = v;
            }
        }

        await RefreshEntitiesAsync<DriverWorkflow, DriverState>(
            client, "WorkflowType = 'DriverWorkflow' AND ExecutionStatus = 'Running'", temp, wf => wf.GetState(), 200);

        lock (drivers)
        {
            drivers.Clear();
            foreach (var (k, v) in temp)
            {
                drivers[k] = new DriverView
                {
                    WorkflowId = v.WorkflowId,
                    RunId = v.RunId,
                    ExecutionStatus = v.ExecutionStatus,
                    StartTime = v.StartTime,
                    CloseTime = v.CloseTime,
                    Stale = v.Stale,
                    State = v.State,
                };
            }
        }
    }

    private async Task RefreshSyncAsync(TemporalClient client)
    {
        WorkflowExecution? latest = null;
        await foreach (var wf in client.ListWorkflowsAsync("WorkflowType = 'EquipmentSyncWorkflow' AND ExecutionStatus = 'Completed'"))
        {
            if (latest == null || (wf.CloseTime ?? wf.StartTime) > (latest.CloseTime ?? latest.StartTime))
            {
                latest = wf;
            }
        }

        if (latest == null || sync?.RunId == latest.RunId)
        {
            return;
        }

        try
        {
            var report = await client.GetWorkflowHandle(latest.Id, latest.RunId).GetResultAsync<SyncReport>();
            sync = new SyncView { WorkflowId = latest.Id, RunId = latest.RunId, Report = report };
        }
        catch (Exception e)
        {
            log.LogDebug("Could not read sync result: {Message}", e.Message);
        }
    }

    private static async Task<TState?> TryQueryAsync<TWorkflow, TState>(
        TemporalClient client, string workflowId, Expression<Func<TWorkflow, TState>> query)
        where TState : class
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        try
        {
            return await client.GetWorkflowHandle<TWorkflow>(workflowId).QueryAsync(
                query, new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cts.Token } });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Describe(WorkflowExecutionStatus status) => status switch
    {
        WorkflowExecutionStatus.Running => "Running",
        WorkflowExecutionStatus.Completed => "Completed",
        WorkflowExecutionStatus.Failed => "Failed",
        WorkflowExecutionStatus.Canceled => "Canceled",
        WorkflowExecutionStatus.Terminated => "Terminated",
        WorkflowExecutionStatus.ContinuedAsNew => "ContinuedAsNew",
        WorkflowExecutionStatus.TimedOut => "TimedOut",
        _ => status.ToString(),
    };

    private FleetSnapshot Build(
        TelematicsFleetDto? fleet, JsonNode? chaos, Dictionary<string, TmsEquipmentStatus> tms, bool temporalConnected)
    {
        List<EntityView<TruckState>> truckViews;
        List<DriverView> driverViews;
        List<EntityView<LoadState>> loadViews;
        List<EntityView<MaintenanceState>> repairViews;
        List<EntityView<SafetyCoachingState>> coachingViews;
        lock (trucks)
        {
            truckViews = trucks.Values.ToList();
        }

        lock (drivers)
        {
            driverViews = drivers.Values.ToList();
        }

        lock (loads)
        {
            loadViews = loads.Values.OrderByDescending(l => l.StartTime).ToList();
        }

        lock (repairs)
        {
            repairViews = repairs.Values.OrderByDescending(r => r.StartTime).ToList();
        }

        lock (coaching)
        {
            coachingViews = coaching.Values.OrderByDescending(c => c.StartTime).ToList();
        }

        var eldByDriver = (fleet?.Drivers ?? new List<TelematicsDriverDto>()).ToDictionary(d => d.DriverId);
        foreach (var d in driverViews)
        {
            d.Eld = d.State != null && eldByDriver.TryGetValue(d.State.DriverId, out var eld) ? eld : null;
        }

        var driverNames = driverViews.Where(d => d.State != null).ToDictionary(d => d.State!.DriverId, d => d.State!.Name);
        var entityByUnit = truckViews.Where(t => t.State != null).ToDictionary(t => t.State!.Unit);
        var units = (fleet?.Trucks.Select(t => t.Unit) ?? Enumerable.Empty<string>())
            .Concat(entityByUnit.Keys)
            .Distinct()
            .OrderBy(u => u, StringComparer.Ordinal);

        var truckList = units.Select(unit =>
        {
            entityByUnit.TryGetValue(unit, out var entity);
            var tel = fleet?.Trucks.FirstOrDefault(t => t.Unit == unit);
            tms.TryGetValue(unit, out var m);
            var driverId = tel?.DriverId ?? entity?.State?.CurrentDriverId;
            return new TruckView
            {
                Unit = unit,
                WorkflowId = WorkflowIds.Truck(unit),
                RunId = entity?.RunId,
                Stale = entity?.Stale ?? true,
                Telematics = tel,
                Entity = entity?.State,
                Tms = m,
                DriverName = driverId != null && driverNames.TryGetValue(driverId, out var name) ? name : null,
            };
        }).ToList();

        var kpis = new Kpis
        {
            TrucksAvailable = truckList.Count(t => t.Entity?.Status == TruckStatus.Available),
            TrucksOnLoad = truckList.Count(t => t.Entity?.Status is TruckStatus.Reserved or TruckStatus.OnLoad),
            TrucksInShop = truckList.Count(t => t.Entity?.Status is TruckStatus.ShopBound or TruckStatus.InShop),
            TrucksDown = truckList.Count(t => t.Entity?.Status is TruckStatus.Breakdown or TruckStatus.OutOfService),
            ActiveLoads = loadViews.Count(l => l.ExecutionStatus == "Running"),
            DeliveredLoads = loadViews.Count(l => l.State?.Status == LoadStatus.Delivered),
            Repowers = loadViews.Sum(l => l.State?.RepowerCount ?? 0),
            OpenRepairs = repairViews.Count(r => r.ExecutionStatus == "Running"),
            CompletedRepairs = repairViews.Count(r => r.State?.Phase == MaintenancePhase.Completed),
            PartnerRepairs = repairViews.Count(r => r.State?.Phase == MaintenancePhase.Completed && r.State.SelectedShop?.IsPartner == true),
            SavingsCaptured = repairViews.Where(r => r.State?.Phase == MaintenancePhase.Completed).Sum(r => r.State!.Savings),
            PendingApprovals = repairViews.Count(r => r.ExecutionStatus == "Running" && r.State?.Phase == MaintenancePhase.AwaitingApproval),
            DuplicatesAbsorbed = repairViews.Sum(r => r.State?.DuplicateEventsIgnored ?? 0) +
                                 truckList.Sum(t => t.Entity?.AnomaliesMergedIntoOpenCase ?? 0),
            DriversOnboarding = driverViews.Count(d => d.State?.Employment == DriverEmployment.Onboarding),
            SafetyCallsHeld = driverViews.Count(d => d.State?.PendingSafetyEvents.Count > 0),
        };

        var inbox = new List<InboxItem>();
        foreach (var r in repairViews.Where(r => r.ExecutionStatus == "Running" && r.State?.Phase == MaintenancePhase.AwaitingApproval))
        {
            var s = r.State!;
            var shop = s.Options.FirstOrDefault(o => o.ShopId == s.Recommendation?.ShopId);
            inbox.Add(new InboxItem
            {
                Kind = "approval",
                Id = s.MaintenanceId,
                WorkflowId = r.WorkflowId,
                RunId = r.RunId,
                Title = $"Approve {s.Category.ToString().ToLowerInvariant()} repair for unit {s.Unit}",
                Detail = $"{s.Approval?.Reason} Recommended: {shop?.Name ?? s.Recommendation?.ShopId} (${shop?.EstimatedCost:N0}).",
                Since = s.Approval?.RequestedAt ?? s.StartedAt,
            });
        }

        foreach (var c in coachingViews.Where(c => c.ExecutionStatus == "Running" && c.State?.Status == CoachingStatus.Escalated))
        {
            inbox.Add(new InboxItem
            {
                Kind = "coaching",
                Id = c.State!.CoachingId,
                WorkflowId = c.WorkflowId,
                RunId = c.RunId,
                Title = $"Follow up with {c.State.DriverName} on a safety event",
                Detail = "Three automated calls went unanswered.",
                Since = c.State.StartedAt,
            });
        }

        foreach (var l in loadViews.Where(l => l.ExecutionStatus == "Running" && l.State?.NeedsAttention == true))
        {
            inbox.Add(new InboxItem
            {
                Kind = "capacity",
                Id = l.State!.LoadId,
                WorkflowId = l.WorkflowId,
                RunId = l.RunId,
                Title = $"Load {l.State.LoadId} has no truck yet",
                Detail = $"{l.State.PlanningAttempts} planning attempts. {l.State.PlanningLog.LastOrDefault()?.Text}",
                Since = l.State.StatusSince,
            });
        }

        return new FleetSnapshot
        {
            GeneratedAt = DateTime.UtcNow,
            SimTime = fleet?.SimTime,
            TimeScale = fleet?.TimeScale ?? DemoSettings.DefaultTimeScale,
            TemporalConnected = temporalConnected,
            ExternalSystemsConnected = fleet != null,
            TemporalUi = temporal.Settings.UiBaseUrl.TrimEnd('/'),
            Namespace = temporal.Settings.Namespace,
            AutoTender = controls.AutoTender,
            Kpis = kpis,
            Trucks = truckList,
            Drivers = driverViews.OrderBy(d => d.WorkflowId, StringComparer.Ordinal).ToList(),
            Loads = loadViews,
            Repairs = repairViews,
            Coaching = coachingViews,
            Inbox = inbox.OrderBy(i => i.Since).ToList(),
            Workers = workers.View(),
            Chaos = chaos,
            Sync = sync,
        };
    }
}
