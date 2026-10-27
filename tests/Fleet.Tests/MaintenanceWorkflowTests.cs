using Fleet.Core;
using Fleet.Workflows;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace Fleet.Tests;

/// <summary>
/// Hours of business time in a couple of seconds: the time-skipping test server fires the 2-hour vendor
/// timer instantly. External systems are replaced by activity mocks with the same names.
/// The first run downloads the Temporal test server binary, so it needs internet access once.
/// </summary>
public class MaintenanceWorkflowTests
{
    [Fact]
    public async Task UnresponsiveVendorEscalatesAndOutOfOrderDuplicateCompletionIsHandled()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var queue = $"test-{Guid.NewGuid()}";
        var mocks = new MaintenanceMocks();
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(queue).AddWorkflow<MaintenanceWorkflow>().AddAllActivities(mocks));

        await worker.ExecuteAsync(async () =>
        {
            var input = new MaintenanceInput
            {
                MaintenanceId = $"maint-test-{Guid.NewGuid():N}",
                Unit = "41209",
                Trigger = MaintenanceTrigger.Predictive,
                Anomaly = new Anomaly("a1", "41209", "SPN241-TIRE-LOW", IssueCategory.Tires, Severity.Medium,
                    "Slow leak", 80, DateTime.UtcNow, false, 39.1, -94.6),
                // The time-skipping test server has no custom search attributes registered.
                Settings = new DemoSettings { TimeScale = 1, IntegrationsQueue = queue, UseSearchAttributes = false },
            };
            var handle = await env.Client.StartWorkflowAsync(
                (MaintenanceWorkflow wf) => wf.RunAsync(input),
                new(id: input.MaintenanceId, taskQueue: queue));

            // Vendor A is asked first and never answers. Skip past its 2-hour window.
            await WaitForAsync(handle, s => s.VendorAttempts.Count == 1);
            await env.DelayAsync(TimeSpan.FromHours(2.5));

            // Vendor B declines.
            var state = await WaitForAsync(handle, s => s.VendorAttempts.Count == 2);
            Assert.Equal("Timed out", state.VendorAttempts[0].Outcome);
            var second = state.VendorAttempts[1];
            await handle.SignalAsync(wf => wf.VendorRespondedAsync(new VendorResponse(second.RequestId, second.ShopId, false, 0, "No bay")));

            // The partner workshop accepts.
            state = await WaitForAsync(handle, s => s.VendorAttempts.Count == 3);
            var third = state.VendorAttempts[2];
            Assert.Equal("P-OMA", third.ShopId);
            await handle.SignalAsync(wf => wf.VendorRespondedAsync(new VendorResponse(third.RequestId, third.ShopId, true, 1.5, null)));
            await WaitForAsync(handle, s => s.Phase == MaintenancePhase.EnRouteToShop);

            // Completion arrives twice, and before the arrival event. Out of order and duplicated.
            var completion = new RepairCompletion("done-1", third.RequestId, third.ShopId, 430m, "Tire replaced");
            await handle.SignalAsync(wf => wf.RepairCompletedAsync(completion));
            await handle.SignalAsync(wf => wf.RepairCompletedAsync(completion));
            await handle.SignalAsync(wf => wf.ArrivedAtShopAsync(new GeofenceEvent("g1", "41209", "Omaha Partner Workshop", 41.26, -95.93, DateTime.UtcNow)));

            var result = await handle.GetResultAsync();
            Assert.Equal("Completed", result.Outcome);
            Assert.True(result.IsPartner);
            Assert.Equal(1020m, result.Savings);

            var final = await handle.QueryAsync(wf => wf.GetState());
            Assert.Equal(new[] { "Timed out", "Declined", "Accepted" }, final.VendorAttempts.Select(v => v.Outcome));
            Assert.Equal(1, final.DuplicateEventsIgnored);
            Assert.Equal(new[] { "G-A", "G-B", "P-OMA" }, mocks.RequestedShops);
        });
    }

    private static async Task<MaintenanceState> WaitForAsync(
        WorkflowHandle<MaintenanceWorkflow> handle, Func<MaintenanceState, bool> condition)
    {
        for (var i = 0; i < 150; i++)
        {
            MaintenanceState state;
            try
            {
                state = await handle.QueryAsync(wf => wf.GetState());
            }
            catch (Temporalio.Exceptions.RpcException e)
            {
                // A query only times out like this when the workflow task itself is failing. Say why.
                throw new InvalidOperationException(
                    $"Query failed ({e.Message}). Last workflow task failure: {await LastWorkflowTaskFailureAsync(handle)}", e);
            }

            if (condition(state))
            {
                return state;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Workflow did not reach the expected state. Last workflow task failure: {await LastWorkflowTaskFailureAsync(handle)}");
    }

    private static async Task<string> LastWorkflowTaskFailureAsync(WorkflowHandle handle)
    {
        try
        {
            var history = await handle.FetchHistoryAsync();
            var failed = history.Events.LastOrDefault(e => e.WorkflowTaskFailedEventAttributes != null);
            return failed?.WorkflowTaskFailedEventAttributes.Failure?.Message ?? "none";
        }
        catch (Exception e)
        {
            return $"(could not read history: {e.Message})";
        }
    }

    /// <summary>Activity mocks. Names match the real activities (method name without the Async suffix).</summary>
    public class MaintenanceMocks
    {
        private static readonly List<ShopOption> Options = new()
        {
            new("G-A", "Vendor A", "MKC", "Kansas City, MO", false, 15, 1450m, 1450m, 1.0, 1, ""),
            new("G-B", "Vendor B", "DSM", "Des Moines, IA", false, 190, 1450m, 1450m, 3.5, 2, ""),
            new("P-OMA", "Omaha Partner Workshop", "OMA", "Omaha, NE", true, 190, 435m, 1450m, 3.5, 3, ""),
        };

        public List<string> RequestedShops { get; } = new();

        [Activity("PlaceMaintenanceHold")]
        public MaintenanceHoldResult PlaceMaintenanceHold(string unit, MaintenanceHoldRequest request) => new(null, false);

        [Activity("CancelMaintenanceHold")]
        public void CancelMaintenanceHold(string unit, string maintenanceId)
        {
        }

        [Activity("UpsertWorkOrder")]
        public WorkOrderRef UpsertWorkOrder(WorkOrderUpsert upsert) => new("0WO000000000001AAA", "00001001", true);

        [Activity("FindShopOptions")]
        public List<ShopOption> FindShopOptions(ShopSearchRequest request) => Options;

        [Activity("RecommendShopWithAi")]
        public Recommendation? RecommendShopWithAi(AdvisorRequest request) => null;

        [Activity("RecommendShopWithPolicy")]
        public Recommendation RecommendShopWithPolicy(AdvisorRequest request) =>
            new("G-A", "Closest shop.", "Policy engine", 0.8, false, null);

        [Activity("RequestShop")]
        public void RequestShop(ShopRequest request)
        {
            lock (RequestedShops)
            {
                RequestedShops.Add(request.ShopId);
            }
        }

        [Activity("CancelShopRequest")]
        public void CancelShopRequest(string requestId)
        {
        }

        [Activity("DispatchToShop")]
        public void DispatchToShop(ShopDispatch dispatch)
        {
        }

        [Activity("MarkTruckInShop")]
        public void MarkTruckInShop(string unit, string maintenanceId)
        {
        }

        [Activity("NotifyShopArrival")]
        public void NotifyShopArrival(string requestId)
        {
        }

        [Activity("CompleteTruckMaintenance")]
        public bool CompleteTruckMaintenance(string unit, MaintenanceCompletion completion) => true;
    }
}
