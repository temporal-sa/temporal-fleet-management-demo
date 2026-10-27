namespace Fleet.Core;

public class DriverInput
{
    public string DriverId { get; set; } = "";
    public string Name { get; set; } = "";
    public string HomeHub { get; set; } = "";
    public List<string> Endorsements { get; set; } = new();
    public DriverEmployment Employment { get; set; } = DriverEmployment.Active;
    public string? SalesforceId { get; set; }
    public string Phone { get; set; } = "";
    public string? PairedUnit { get; set; }
    public DemoSettings Settings { get; set; } = new();
    public DriverState? CarryOver { get; set; }
}

/// <summary>Hours-of-service clocks as reported by the ELD.</summary>
public record HosClock(double DriveMinutesLeft, double ShiftMinutesLeft, double CycleMinutesLeft, double MinutesSinceBreak)
{
    public static HosClock Fresh() => new(660, 840, 4200, 0);
}

public class OnboardingStep
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public double SimMinutes { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class DriverState
{
    public string DriverId { get; set; } = "";
    public string Name { get; set; } = "";
    public string HomeHub { get; set; } = "";
    public List<string> Endorsements { get; set; } = new();
    public DriverEmployment Employment { get; set; }
    public DutyStatus Duty { get; set; } = DutyStatus.OffDuty;
    public DateTime DutySince { get; set; }
    public HosClock Hos { get; set; } = HosClock.Fresh();
    public string? CurrentLoadId { get; set; }
    public string? CurrentUnit { get; set; }
    public string Phone { get; set; } = "";
    public string? SalesforceId { get; set; }
    public List<OnboardingStep> Onboarding { get; set; } = new();
    public List<SafetyEvent> PendingSafetyEvents { get; set; } = new();
    public List<string> SeenSafetyEventIds { get; set; } = new();
    public List<string> CoachingWorkflowIds { get; set; } = new();
    public int CoachingSequence { get; set; }
    public int SafetyEventsTotal { get; set; }
    public int LoadsCompleted { get; set; }
    public int RunNumber { get; set; } = 1;
    public List<EntityEvent> RecentEvents { get; set; } = new();
}

public record DutyStatusChange(
    string DriverId, DutyStatus Status, HosClock Hos, string? Unit, double Lat, double Lon, string Reason);

public record SafetyEvent(
    string EventId,
    string DriverId,
    string? Unit,
    SafetyEventType Type,
    Severity Severity,
    double SpeedMph,
    DateTime At,
    string Location);

public record ReserveDriverRequest(string LoadId, string Unit, List<string> RequiredEndorsements, double EstimatedDriveMinutes);

public record ReserveDriverResult(string DriverId, string LoadId);

public record DriverMasterData(string? SalesforceId, string Name, string HomeHub, List<string> Endorsements, string Phone);
