using Fleet.Core;
using Temporalio.Client;

namespace Fleet.Api.Services;

/// <summary>Lazily connects to Temporal and keeps retrying, so the API can start before the server.</summary>
public class TemporalClientProvider
{
    private readonly object gate = new();
    private readonly ILogger<TemporalClientProvider> log;
    private Task<TemporalClient>? connecting;

    public TemporalClientProvider(IConfiguration config, ILogger<TemporalClientProvider> log)
    {
        this.log = log;
        Settings = config.GetSection("Temporal").Get<TemporalSettings>() ?? new TemporalSettings();
    }

    public TemporalSettings Settings { get; }

    public TemporalClient? Client { get; private set; }

    public Task<TemporalClient> GetAsync()
    {
        if (Client != null)
        {
            return Task.FromResult(Client);
        }

        lock (gate)
        {
            connecting ??= ConnectAsync();
            return connecting;
        }
    }

    private async Task<TemporalClient> ConnectAsync()
    {
        var client = await TemporalConnector.ConnectWithRetryAsync(Settings, m => log.LogWarning("{Message}", m), CancellationToken.None);
        log.LogInformation("Connected to Temporal at {Address}", Settings.Address);
        Client = client;
        return client;
    }
}
