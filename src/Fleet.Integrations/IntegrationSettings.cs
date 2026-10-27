namespace Fleet.Integrations;

public class EndpointSettings
{
    /// <summary>Base URL of Fleet.ExternalSystems (the mocks for the TMS, telematics, vendors, IVR and Kafka).</summary>
    public string ExternalSystems { get; set; } = "http://localhost:5050";

    /// <summary>Base URL of Fleet.Api (workers post heartbeats here so the UI can show worker health).</summary>
    public string Api { get; set; } = "http://localhost:5100";
}

public class SalesforceOptions
{
    /// <summary>
    /// For the mock: http://localhost:5050. For a real org: your My Domain URL,
    /// e.g. https://yourcompany--sandbox.sandbox.my.salesforce.com (client credentials flow requires My Domain).
    /// </summary>
    public string LoginUrl { get; set; } = "http://localhost:5050";

    public string ClientId { get; set; } = "fleet-demo-connected-app";
    public string ClientSecret { get; set; } = "demo-secret";
    public string ApiVersion { get; set; } = "v62.0";
}

public class AdvisorOptions
{
    /// <summary>"Policy" (deterministic, works offline) or "Claude" (calls the Anthropic Messages API).</summary>
    public string Provider { get; set; } = "Policy";

    public string Model { get; set; } = "claude-sonnet-5-5";
    public string? AnthropicApiKey { get; set; }
}
