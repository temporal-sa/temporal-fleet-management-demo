using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fleet.Integrations;

public record SalesforceUpsertResult(string Id, bool Created);

/// <summary>
/// A real Salesforce REST API client, not a fake. It speaks the same protocol to the local mock and to a
/// real org: OAuth 2.0 client credentials, SOQL queries with paging, and PATCH upserts by external ID.
/// To go live, change Salesforce:LoginUrl, ClientId and ClientSecret (see docs/SALESFORCE.md).
/// </summary>
public class SalesforceRestClient
{
    private const string SystemName = "Salesforce";

    private readonly HttpClient http;
    private readonly SalesforceOptions options;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? accessToken;
    private string? instanceUrl;

    public SalesforceRestClient(HttpClient http, SalesforceOptions options)
    {
        this.http = http;
        this.options = options;
    }

    private string DataPath => $"/services/data/{options.ApiVersion}";

    /// <summary>Runs a SOQL query and follows nextRecordsUrl until all records are read.</summary>
    public async Task<List<JsonObject>> QueryAsync(string soql, CancellationToken ct)
    {
        var records = new List<JsonObject>();
        string? next = $"{DataPath}/query?q={Uri.EscapeDataString(soql)}";
        while (next != null)
        {
            var page = await SendAsync(HttpMethod.Get, next, null, ct);
            if (page?["records"] is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonObject obj)
                    {
                        records.Add(JsonNode.Parse(obj.ToJsonString())!.AsObject());
                    }
                }
            }

            var done = page?["done"]?.GetValue<bool>() ?? true;
            next = done ? null : page?["nextRecordsUrl"]?.GetValue<string>();
        }

        return records;
    }

    /// <summary>
    /// Upsert by external ID. Retrying the same call never creates a duplicate record, which is what makes
    /// the calling activity safe to retry. Temporal retries; the external ID makes the retry idempotent.
    /// </summary>
    public async Task<SalesforceUpsertResult> UpsertByExternalIdAsync(
        string sobject, string externalIdField, string externalId, IDictionary<string, object?> fields, CancellationToken ct)
    {
        var path = $"{DataPath}/sobjects/{sobject}/{externalIdField}/{Uri.EscapeDataString(externalId)}";
        var result = await SendAsync(HttpMethod.Patch, path, fields, ct);
        var id = result?["id"]?.GetValue<string>() ?? "";
        var created = result?["created"]?.GetValue<bool>() ?? false;
        return new SalesforceUpsertResult(id, created);
    }

    public async Task<JsonObject?> GetRecordAsync(string sobject, string id, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, $"{DataPath}/sobjects/{sobject}/{id}", null, ct)) as JsonObject;

    public async Task UpdateAsync(string sobject, string id, IDictionary<string, object?> fields, CancellationToken ct) =>
        await SendAsync(HttpMethod.Patch, $"{DataPath}/sobjects/{sobject}/{id}", fields, ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string relativePath, object? body, CancellationToken ct, bool isRetry = false)
    {
        await EnsureTokenAsync(ct);

        using var request = new HttpRequestMessage(method, new Uri(new Uri(instanceUrl!), relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ExternalSystemException(SystemName, 0, "CONNECTION_FAILED", $"Salesforce is unreachable: {e.Message}", retryable: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ExternalSystemException(SystemName, 408, "TIMEOUT", "Salesforce did not answer in time", retryable: true);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);

            // Expired session: refresh the token once and replay the request.
            if (response.StatusCode == HttpStatusCode.Unauthorized && !isRetry)
            {
                accessToken = null;
                return await SendAsync(method, relativePath, body, ct, isRetry: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw ExternalSystemException.FromResponse(SystemName, (int)response.StatusCode, text);
            }

            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (accessToken != null && instanceUrl != null)
        {
            return;
        }

        await tokenLock.WaitAsync(ct);
        try
        {
            if (accessToken != null && instanceUrl != null)
            {
                return;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
            });

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync(new Uri(new Uri(options.LoginUrl), "/services/oauth2/token"), form, ct);
            }
            catch (HttpRequestException e)
            {
                throw new ExternalSystemException(SystemName, 0, "CONNECTION_FAILED", $"Salesforce login is unreachable: {e.Message}", retryable: true);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    // invalid_client / invalid_grant are configuration problems and will not fix themselves.
                    var retryable = (int)response.StatusCode >= 500;
                    throw new ExternalSystemException(
                        SystemName, (int)response.StatusCode, "AUTH_FAILED", $"Salesforce token request failed: {text}", retryable);
                }

                var node = JsonNode.Parse(text);
                accessToken = node?["access_token"]?.GetValue<string>();
                instanceUrl = node?["instance_url"]?.GetValue<string>() ?? options.LoginUrl;
            }
        }
        finally
        {
            tokenLock.Release();
        }
    }
}
