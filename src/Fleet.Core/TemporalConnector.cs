using Temporalio.Client;

namespace Fleet.Core;

/// <summary>Connection settings. Works for the local dev server, self-hosted, or Temporal Cloud (API key or mTLS).</summary>
public class TemporalSettings
{
    public string Address { get; set; } = "localhost:7233";
    public string Namespace { get; set; } = "default";
    public string? ApiKey { get; set; }
    public string? TlsCertPath { get; set; }
    public string? TlsKeyPath { get; set; }

    /// <summary>Base URL of the Temporal Web UI, used to build deep links from the fleet UI.</summary>
    public string UiBaseUrl { get; set; } = "http://localhost:8233";

    public bool IsLocalDevServer => Address.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                                    Address.StartsWith("127.0.0.1", StringComparison.Ordinal);
}

public static class TemporalConnector
{
    public static Task<TemporalClient> ConnectAsync(TemporalSettings settings)
    {
        var options = new TemporalClientConnectOptions(settings.Address)
        {
            Namespace = settings.Namespace,
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            options.ApiKey = settings.ApiKey;
            options.Tls = new TlsOptions();
        }
        else if (!string.IsNullOrWhiteSpace(settings.TlsCertPath) && !string.IsNullOrWhiteSpace(settings.TlsKeyPath))
        {
            options.Tls = new TlsOptions
            {
                ClientCert = File.ReadAllBytes(settings.TlsCertPath),
                ClientPrivateKey = File.ReadAllBytes(settings.TlsKeyPath),
            };
        }

        return TemporalClient.ConnectAsync(options);
    }

    /// <summary>Retries until the server is reachable, so processes can start in any order.</summary>
    public static async Task<TemporalClient> ConnectWithRetryAsync(
        TemporalSettings settings, Action<string>? log, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await ConnectAsync(settings);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                attempt++;
                log?.Invoke($"Temporal at {settings.Address} not reachable yet (attempt {attempt}): {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + attempt)), ct);
            }
        }
    }
}
