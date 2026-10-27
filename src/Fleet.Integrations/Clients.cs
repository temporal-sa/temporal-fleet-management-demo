using Fleet.Core;

namespace Fleet.Integrations;

/// <summary>TMS (mocked). Equipment availability, load milestones and driver profiles.</summary>
public class TmsClient : ExternalHttpClient
{
    public TmsClient(HttpClient http) : base(http, "TMS") { }

    public async Task<TmsWriteResult> UpdateEquipmentStatusAsync(TmsEquipmentUpdate update, CancellationToken ct) =>
        await SendAsync<TmsWriteResult>(HttpMethod.Put, $"/tms/api/equipment/{update.Unit}/status", update, ct)
        ?? new TmsWriteResult(true, update.Version, "ok");

    public Task RecordLoadMilestoneAsync(LoadMilestone milestone, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/tms/api/loads/{milestone.LoadId}/milestones", milestone, ct);

    public async Task<List<TmsEquipmentStatus>> GetEquipmentAsync(CancellationToken ct) =>
        await SendAsync<List<TmsEquipmentStatus>>(HttpMethod.Get, "/tms/api/equipment", null, ct) ?? new();

    public Task UpsertDriverAsync(TmsDriverProfile profile, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"/tms/api/drivers/{profile.DriverId}", profile, ct);
}

/// <summary>Telematics and ELD platform (mocked by the simulator in Fleet.ExternalSystems).</summary>
public class TelematicsClient : ExternalHttpClient
{
    public TelematicsClient(HttpClient http) : base(http, "Telematics") { }

    public async Task<TelematicsFleetDto> GetFleetAsync(CancellationToken ct) =>
        await SendAsync<TelematicsFleetDto>(HttpMethod.Get, "/telematics/fleet", null, ct) ?? new TelematicsFleetDto();

    public async Task<TelematicsTruckDto?> GetTruckAsync(string unit, CancellationToken ct) =>
        await SendAsync<TelematicsTruckDto>(HttpMethod.Get, $"/telematics/trucks/{unit}", null, ct);

    public Task DispatchAsync(DispatchRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/telematics/dispatch", request, ct);

    public Task HoldInPlaceAsync(string unit, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/telematics/hold/{unit}", null, ct);
}

/// <summary>Workshop booking portal for partner and general workshops (mocked).</summary>
public class VendorPortalClient : ExternalHttpClient
{
    public VendorPortalClient(HttpClient http) : base(http, "Vendor portal") { }

    public Task RequestServiceAsync(ShopRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/vendors/requests", request, ct);

    public Task CancelAsync(string requestId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/vendors/requests/{requestId}/cancel", null, ct);

    public Task NotifyArrivalAsync(string requestId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/vendors/requests/{requestId}/arrived", null, ct);
}

/// <summary>Outbound driver calls (mocked IVR).</summary>
public class IvrClient : ExternalHttpClient
{
    public IvrClient(HttpClient http) : base(http, "IVR") { }

    public Task PlaceCallAsync(CoachingCall call, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/ivr/calls", call, ct);
}

/// <summary>Publishes events to downstream consumers (a Kafka topic stand-in).</summary>
public class KafkaClient : ExternalHttpClient
{
    public KafkaClient(HttpClient http) : base(http, "Kafka") { }

    public Task PublishAsync(DownstreamEvent evt, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/kafka/topics/{evt.Topic}", new { key = evt.Key, value = evt.PayloadJson }, ct);
}
