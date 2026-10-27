namespace Fleet.Core;

public class SafetyCoachingInput
{
    public string CoachingId { get; set; } = "";
    public string DriverId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string Phone { get; set; } = "";
    public List<SafetyEvent> Events { get; set; } = new();
    public DemoSettings Settings { get; set; } = new();
}

public class SafetyCoachingState
{
    public string CoachingId { get; set; } = "";
    public string DriverId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public CoachingStatus Status { get; set; } = CoachingStatus.Calling;
    public int CallAttempts { get; set; }
    public string? CaseId { get; set; }
    public List<SafetyEvent> Events { get; set; } = new();
    public bool NeedsAttention { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime StartedAt { get; set; }
    public List<EntityEvent> Timeline { get; set; } = new();
}

public record CoachingCall(string CallId, string WorkflowId, string DriverId, string Phone, string Script);

public record SafetyCaseUpsert(string ExternalId, string DriverId, string Subject, string Description, string Status, string Priority);

public record CoachingResolution(string ResolvedBy, string Note);
