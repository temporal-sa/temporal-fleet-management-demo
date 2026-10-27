using System.Collections.Concurrent;
using Fleet.Core;

namespace Fleet.Api.Services;

public record WorkerHeartbeat(string Identity, List<string> Queues, int Pid, DateTime StartedAt);

public record WorkerInstanceView(string Identity, int Pid, double SecondsSinceSeen);

public record WorkerQueueView(string Queue, bool Online, List<WorkerInstanceView> Instances);

/// <summary>Tracks worker processes by heartbeat so the UI can show a queue going dark when its worker is killed.</summary>
public class WorkerRegistry
{
    private readonly ConcurrentDictionary<string, (WorkerHeartbeat Beat, DateTime Seen)> workers = new();

    public void Heartbeat(WorkerHeartbeat beat) => workers[beat.Identity] = (beat, DateTime.UtcNow);

    public void Goodbye(string identity) => workers.TryRemove(identity, out _);

    public List<WorkerQueueView> View()
    {
        var now = DateTime.UtcNow;
        foreach (var stale in workers.Where(w => now - w.Value.Seen > TimeSpan.FromMinutes(2)).Select(w => w.Key).ToList())
        {
            workers.TryRemove(stale, out _);
        }

        return TaskQueues.All.Select(queue =>
        {
            var instances = workers.Values
                .Where(w => w.Beat.Queues.Contains(queue))
                .Select(w => new WorkerInstanceView(w.Beat.Identity, w.Beat.Pid, Math.Round((now - w.Seen).TotalSeconds, 1)))
                .OrderBy(i => i.SecondsSinceSeen)
                .ToList();
            return new WorkerQueueView(queue, instances.Any(i => i.SecondsSinceSeen < 6), instances);
        }).ToList();
    }
}
