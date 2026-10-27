using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Core;

namespace Fleet.Integrations;

/// <summary>
/// Asks Claude to pick a repair shop from the ranked options and explain why. The model only recommends;
/// approval rules still come from <see cref="MaintenancePolicy"/>. Retries are left to Temporal
/// (this client does not retry), so a 429 or 5xx becomes an activity retry that shows up in history.
/// </summary>
public class ClaudeAdvisor
{
    private readonly HttpClient http;
    private readonly AdvisorOptions options;

    public ClaudeAdvisor(HttpClient http, AdvisorOptions options)
    {
        this.http = http;
        this.options = options;
    }

    public bool IsConfigured =>
        string.Equals(options.Provider, "Claude", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ApiKey);

    private string? ApiKey => !string.IsNullOrWhiteSpace(options.AnthropicApiKey)
        ? options.AnthropicApiKey
        : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    public async Task<Recommendation> RecommendAsync(AdvisorRequest req, CancellationToken ct)
    {
        var optionsJson = JsonSerializer.Serialize(req.Options.Select(o => new
        {
            o.ShopId,
            o.Name,
            o.City,
            o.IsPartner,
            o.DistanceMiles,
            o.EstimatedCost,
            o.EtaHours,
            o.Notes,
        }));

        var prompt =
            "You are a fleet maintenance advisor for a truckload carrier. Pick exactly one repair shop from the options.\n" +
            "Partner workshops (IsPartner=true) cost about 70% less than general workshops, so prefer them when the truck can safely get there.\n" +
            "Higher severity means less distance is acceptable. Roadside events need the closest capable shop.\n\n" +
            $"Truck: {req.Unit}\nIssue: {req.Category}, severity {req.Severity}, roadside={req.Roadside}\nDetails: {req.Description}\n" +
            $"Options (JSON): {optionsJson}\n\n" +
            "Reply with only a JSON object and no other text: {\"shopId\": \"...\", \"rationale\": \"two sentences for a fleet manager\", \"confidence\": 0.0-1.0}";

        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["max_tokens"] = 400,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ExternalSystemException("Claude", 0, "CONNECTION_FAILED", e.Message, retryable: true);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw ExternalSystemException.FromResponse("Claude", (int)response.StatusCode, text);
            }

            var reply = JsonNode.Parse(text)?["content"]?.AsArray()
                .Select(c => c?["text"]?.GetValue<string>())
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
            var start = reply.IndexOf('{');
            var end = reply.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                throw new ExternalSystemException("Claude", 200, "BAD_FORMAT", "Model reply was not JSON", retryable: true);
            }

            var parsed = JsonNode.Parse(reply[start..(end + 1)]);
            var shopId = parsed?["shopId"]?.GetValue<string>() ?? "";
            var chosen = req.Options.FirstOrDefault(o => o.ShopId == shopId);
            if (chosen == null)
            {
                throw new ExternalSystemException("Claude", 200, "UNKNOWN_SHOP", $"Model picked unknown shop '{shopId}'", retryable: true);
            }

            var rationale = parsed?["rationale"]?.GetValue<string>() ?? "";
            var confidence = parsed?["confidence"]?.GetValue<double>() ?? 0.7;
            var (required, reason) = MaintenancePolicy.ApprovalRule(chosen, req.Roadside, req.ApprovalThreshold);
            return new Recommendation(chosen.ShopId, rationale, $"Claude ({options.Model})", confidence, required, reason);
        }
    }
}
