using System.Threading.Channels;
using Fleet.Core;
using Temporalio.Client;

namespace Fleet.ExternalSystems.Infrastructure;

/// <summary>
/// The only Temporal code in the "existing systems" process. In production this is a small Kafka consumer
/// (or webhook handler) that turns a meaningful event into a signal: "arrived at pickup", "anomaly detected",
/// "vendor accepted". Signals are addressed by workflow ID, which is the business key.
/// </summary>
public class TemporalBridge : BackgroundService
{
    private readonly Channel<PendingSignal> channel = Channel.CreateUnbounded<PendingSignal>();
    private readonly TemporalSettings settings;
    private readonly EventLog log;
    private readonly ILogger<TemporalBridge> logger;

    public TemporalBridge(IConfiguration config, EventLog log, ILogger<TemporalBridge> logger)
    {
        settings = config.GetSection("Temporal").Get<TemporalSettings>() ?? new TemporalSettings();
        this.log = log;
        this.logger = logger;
    }

    public bool Connected { get; private set; }

    /// <summary>Queues a signal. Non-blocking; safe to call while holding the simulator lock.</summary>
    public void Signal(string workflowId, string signalName, object arg, string? description = null) =>
        channel.Writer.TryWrite(new PendingSignal(workflowId, signalName, arg, description));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = await TemporalConnector.ConnectWithRetryAsync(
            settings, m => logger.LogWarning("{Message}", m), stoppingToken);
        Connected = true;
        logger.LogInformation("Signal bridge connected to Temporal at {Address}", settings.Address);

        await foreach (var pending in channel.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                try
                {
                    await client.GetWorkflowHandle(pending.WorkflowId)
                        .SignalAsync(pending.SignalName, new object?[] { pending.Arg });
                    if (pending.Description != null)
                    {
                        log.Add("Bridge", "signal", $"{pending.Description} (signal {pending.SignalName} to {pending.WorkflowId})", pending.WorkflowId);
                    }

                    break;
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    if (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) || attempt == 6)
                    {
                        if (pending.Description != null)
                        {
                            log.Add("Bridge", "warn", $"Signal {pending.SignalName} to {pending.WorkflowId} not delivered: {e.Message}", pending.WorkflowId);
                        }

                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }
    }

    private record PendingSignal(string WorkflowId, string SignalName, object Arg, string? Description);
}
