using Fleet.Core;
using Fleet.ExternalSystems.Infrastructure;

namespace Fleet.ExternalSystems.Misc;

public record KafkaMessage(long Offset, string Key, string Value, DateTime At);

/// <summary>A topic store standing in for a Kafka cluster. Workflows publish hand-offs here (invoice, detention).</summary>
public class KafkaStore
{
    private readonly object gate = new();
    private readonly SortedDictionary<string, List<KafkaMessage>> topics = new(StringComparer.Ordinal);

    public long Publish(string topic, string key, string value)
    {
        lock (gate)
        {
            if (!topics.TryGetValue(topic, out var list))
            {
                list = new List<KafkaMessage>();
                topics[topic] = list;
            }

            var offset = list.Count;
            list.Add(new KafkaMessage(offset, key, value, DateTime.UtcNow));
            return offset;
        }
    }

    public object Snapshot()
    {
        lock (gate)
        {
            return topics.Select(kv => new
            {
                topic = kv.Key,
                count = kv.Value.Count,
                recent = kv.Value.TakeLast(12).Reverse().ToList(),
            }).ToList();
        }
    }
}

public class IvrCall
{
    public CoachingCall Call { get; set; } = null!;
    public DateTime PlacedAt { get; set; }
    public string Outcome { get; set; } = "Ringing";
}

/// <summary>Outbound driver calls. "Pressing 1" signals the coaching workflow.</summary>
public class IvrService
{
    private readonly object gate = new();
    private readonly List<IvrCall> calls = new();
    private readonly ChaosState chaos;
    private readonly EventLog log;
    private readonly TemporalBridge bridge;
    private readonly Random rng = new();

    public IvrService(ChaosState chaos, EventLog log, TemporalBridge bridge)
    {
        this.chaos = chaos;
        this.log = log;
        this.bridge = bridge;
    }

    public void Place(CoachingCall call)
    {
        IvrCall record;
        lock (gate)
        {
            if (calls.Any(c => c.Call.CallId == call.CallId))
            {
                return; // Idempotent: a retried activity does not ring the driver twice.
            }

            record = new IvrCall { Call = call, PlacedAt = DateTime.UtcNow };
            calls.Add(record);
            if (calls.Count > 100)
            {
                calls.RemoveAt(0);
            }
        }

        log.Add("IVR", "info", $"Calling driver {call.DriverId} at {call.Phone}.", call.WorkflowId);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4 + rng.NextDouble() * 4));
            lock (gate)
            {
                if (record.Outcome != "Ringing")
                {
                    return;
                }

                record.Outcome = !chaos.IvrNoAnswer && rng.NextDouble() < 0.75 ? "Pressed 1" : "No answer";
            }

            if (record.Outcome == "Pressed 1")
            {
                bridge.Signal(call.WorkflowId, Signals.CallAcknowledged, call.CallId, $"Driver {call.DriverId} pressed 1");
            }
            else
            {
                log.Add("IVR", "warn", $"Driver {call.DriverId} did not answer.", call.WorkflowId);
            }
        });
    }

    public bool Acknowledge(string callId)
    {
        IvrCall? record;
        lock (gate)
        {
            record = calls.FirstOrDefault(c => c.Call.CallId == callId);
            if (record == null)
            {
                return false;
            }

            record.Outcome = "Pressed 1";
        }

        bridge.Signal(record.Call.WorkflowId, Signals.CallAcknowledged, callId, $"Driver {record.Call.DriverId} pressed 1");
        return true;
    }

    public List<IvrCall> Recent()
    {
        lock (gate)
        {
            return calls.AsEnumerable().Reverse().Take(40).ToList();
        }
    }
}

public static class MiscEndpoints
{
    public static void MapKafkaAndIvr(this WebApplication app)
    {
        var kafka = app.Services.GetRequiredService<KafkaStore>();
        var ivr = app.Services.GetRequiredService<IvrService>();
        var log = app.Services.GetRequiredService<EventLog>();

        app.MapPost("/kafka/topics/{topic}", (string topic, KafkaPublish body) =>
        {
            var offset = kafka.Publish(topic, body.Key, body.Value);
            log.Add("Kafka", "info", $"Published {topic} key={body.Key} at offset {offset}.", null);
            return Results.Json(new { topic, offset });
        });

        app.MapGet("/kafka/topics", () => Results.Json(kafka.Snapshot()));

        app.MapPost("/ivr/calls", (CoachingCall call) =>
        {
            ivr.Place(call);
            return Results.Accepted();
        });

        app.MapGet("/ivr/calls", () => Results.Json(ivr.Recent()));

        app.MapPost("/ivr/calls/{callId}/acknowledge", (string callId) =>
            ivr.Acknowledge(callId) ? Results.Ok() : Results.NotFound());
    }

    public record KafkaPublish(string Key, string Value);
}
