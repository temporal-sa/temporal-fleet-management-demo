using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fleet.Core;

namespace Fleet.Api.Services;

/// <summary>Talks to Fleet.ExternalSystems for map data, chaos switches, logs and scenario triggers.</summary>
public class ExternalSystemsProxy
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient http;

    public ExternalSystemsProxy(IConfiguration config)
    {
        http = new HttpClient
        {
            BaseAddress = new Uri(config["Endpoints:ExternalSystems"] ?? "http://localhost:5050"),
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public async Task<T?> GetAsync<T>(string path)
    {
        try
        {
            return await http.GetFromJsonAsync<T>(path, Json);
        }
        catch (Exception)
        {
            return default;
        }
    }

    public Task<JsonNode?> GetNodeAsync(string path) => GetAsync<JsonNode>(path);

    public async Task<(int Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            }

            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            JsonNode? node = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    node = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    node = JsonValue.Create(text);
                }
            }

            return ((int)response.StatusCode, node);
        }
        catch (Exception e)
        {
            return (503, JsonValue.Create($"External systems unreachable: {e.Message}"));
        }
    }

    public Task<(int Status, JsonNode? Body)> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body);

    public async Task LogAsync(string system, string kind, string message, string? workflowId = null) =>
        await SendAsync(HttpMethod.Post, "/events", new { system, kind, message, workflowId });

    public Task<TelematicsFleetDto?> GetFleetAsync() => GetAsync<TelematicsFleetDto>("/telematics/fleet");
}
