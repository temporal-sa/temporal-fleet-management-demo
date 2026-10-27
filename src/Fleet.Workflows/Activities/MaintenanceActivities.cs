using Fleet.Core;
using Fleet.Integrations;
using Temporalio.Activities;

namespace Fleet.Workflows.Activities;

public class MaintenanceActivities
{
    private readonly TelematicsClient telematics;
    private readonly VendorPortalClient vendors;
    private readonly ClaudeAdvisor advisor;

    public MaintenanceActivities(TelematicsClient telematics, VendorPortalClient vendors, ClaudeAdvisor advisor)
    {
        this.telematics = telematics;
        this.vendors = vendors;
        this.advisor = advisor;
    }

    [Activity]
    public async Task<GeoPoint> GetTruckPositionAsync(string unit)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var truck = await ExternalCall.RunAsync(() => telematics.GetTruckAsync(unit, ct));
        return truck == null ? new GeoPoint(0, 0) : new GeoPoint(truck.Lat, truck.Lon);
    }

    [Activity]
    public async Task<List<ShopOption>> FindShopOptionsAsync(ShopSearchRequest request)
    {
        var position = await GetTruckPositionAsync(request.Unit);
        return ShopFinder.FindOptions(request, position.Lat, position.Lon);
    }

    /// <summary>
    /// Returns null when no AI provider is configured, so the workflow uses the policy engine.
    /// When configured, failures are thrown and retried by Temporal (the HTTP client does not retry).
    /// </summary>
    [Activity]
    public async Task<Recommendation?> RecommendShopWithAiAsync(AdvisorRequest request)
    {
        if (!advisor.IsConfigured)
        {
            return null;
        }

        var ct = ActivityExecutionContext.Current.CancellationToken;
        return await ExternalCall.RunAsync(() => advisor.RecommendAsync(request, ct));
    }

    [Activity]
    public Task<Recommendation> RecommendShopWithPolicyAsync(AdvisorRequest request) =>
        Task.FromResult(MaintenancePolicy.Recommend(request));

    [Activity]
    public Task RequestShopAsync(ShopRequest request)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => vendors.RequestServiceAsync(request, ct));
    }

    [Activity]
    public Task CancelShopRequestAsync(string requestId)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => vendors.CancelAsync(requestId, ct));
    }

    [Activity]
    public Task NotifyShopArrivalAsync(string requestId)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => vendors.NotifyArrivalAsync(requestId, ct));
    }

    [Activity]
    public Task HoldTruckInPlaceAsync(string unit)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => telematics.HoldInPlaceAsync(unit, ct));
    }

    [Activity]
    public Task DispatchToShopAsync(ShopDispatch dispatch)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var request = new DispatchRequest(
            dispatch.Unit,
            null,
            dispatch.WorkflowId,
            dispatch.Towed ? $"Tow to {dispatch.ShopName}" : $"To {dispatch.ShopName}",
            dispatch.Towed,
            new List<DispatchStop>
            {
                new(dispatch.ShopName, dispatch.Lat, dispatch.Lon, "shop", Signals.ArrivedAtShop, null, 0, true),
            });
        return ExternalCall.RunAsync(() => telematics.DispatchAsync(request, ct));
    }
}
