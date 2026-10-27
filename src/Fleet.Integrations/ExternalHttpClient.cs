using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleet.Integrations;

/// <summary>Small JSON-over-HTTP base class with consistent error classification.</summary>
public abstract class ExternalHttpClient
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient http;
    private readonly string system;

    protected ExternalHttpClient(HttpClient http, string system)
    {
        this.http = http;
        this.system = system;
    }

    protected async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ExternalSystemException(system, 0, "CONNECTION_FAILED", $"{system} is unreachable: {e.Message}", retryable: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ExternalSystemException(system, 408, "TIMEOUT", $"{system} did not answer in time", retryable: true);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw ExternalSystemException.FromResponse(system, (int)response.StatusCode, text);
            }

            if (response.StatusCode == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(text, Json);
        }
    }

    protected Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct) =>
        SendAsync<JsonElement?>(method, path, body, ct);
}
