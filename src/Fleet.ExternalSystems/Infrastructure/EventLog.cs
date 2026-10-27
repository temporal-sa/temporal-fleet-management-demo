namespace Fleet.ExternalSystems.Infrastructure;

public record LogEntry(long Seq, DateTime At, string System, string Kind, string Message, string? WorkflowId);

/// <summary>What the outside world saw and did. Feeds the event stream at the bottom of the fleet UI.</summary>
public class EventLog
{
    private readonly object gate = new();
    private readonly LinkedList<LogEntry> entries = new();
    private long seq;

    public void Add(string system, string kind, string message, string? workflowId = null)
    {
        lock (gate)
        {
            entries.AddLast(new LogEntry(++seq, DateTime.UtcNow, system, kind, message, workflowId));
            while (entries.Count > 600)
            {
                entries.RemoveFirst();
            }
        }
    }

    public List<LogEntry> Since(long after, int limit)
    {
        lock (gate)
        {
            return entries.Where(e => e.Seq > after).TakeLast(limit).ToList();
        }
    }
}

public record ApiCall(DateTime At, string Method, string Path, int Status, string Note);

/// <summary>Per-system log of API calls received, so the UI can show retries hitting a failing system.</summary>
public class CallLog
{
    private readonly object gate = new();
    private readonly LinkedList<ApiCall> calls = new();

    public void Add(string method, string path, int status, string note = "")
    {
        lock (gate)
        {
            calls.AddLast(new ApiCall(DateTime.UtcNow, method, path.Length > 140 ? path[..140] + "..." : path, status, note));
            while (calls.Count > 200)
            {
                calls.RemoveFirst();
            }
        }
    }

    public List<ApiCall> Recent(int limit = 80)
    {
        lock (gate)
        {
            return calls.Reverse().Take(limit).ToList();
        }
    }
}
