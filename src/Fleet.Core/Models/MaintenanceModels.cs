namespace Fleet.Core;

public class MaintenanceInput
{
    public string MaintenanceId { get; set; } = "";
    public string Unit { get; set; } = "";
    public MaintenanceTrigger Trigger { get; set; }
    public Anomaly Anomaly { get; set; } = null!;
    public DemoSettings Settings { get; set; } = new();
}

public class ApprovalInfo
{
    public string Reason { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    public string? Outcome { get; set; }
    public string? Note { get; set; }
}

public class VendorAttempt
{
    public string RequestId { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string ShopName { get; set; } = "";
    public bool IsPartner { get; set; }
    public DateTime RequestedAt { get; set; }
    public string Outcome { get; set; } = "Pending";
    public DateTime? RespondedAt { get; set; }
    public string? Note { get; set; }
}

public class MaintenanceState
{
    public string MaintenanceId { get; set; } = "";
    public string Unit { get; set; } = "";
    public MaintenanceTrigger Trigger { get; set; }
    public MaintenancePhase Phase { get; set; } = MaintenancePhase.Opened;
    public IssueCategory Category { get; set; }
    public Severity Severity { get; set; }
    public string Description { get; set; } = "";
    public string AnomalyCode { get; set; } = "";
    public bool Roadside { get; set; }
    public string? WorkOrderId { get; set; }
    public string? WorkOrderNumber { get; set; }
    public string? DeferredForLoadId { get; set; }
    public string? RepoweredLoadId { get; set; }
    public List<ShopOption> Options { get; set; } = new();
    public Recommendation? Recommendation { get; set; }
    public ApprovalInfo? Approval { get; set; }
    public List<VendorAttempt> VendorAttempts { get; set; } = new();
    public ShopOption? SelectedShop { get; set; }
    public decimal EstimatedCost { get; set; }
    public decimal? ActualCost { get; set; }
    public decimal GeneralWorkshopBaseline { get; set; }
    public decimal Savings { get; set; }
    public List<string> MergedAnomalies { get; set; } = new();
    public int DuplicateEventsIgnored { get; set; }
    public bool NeedsAttention { get; set; }
    public string? CancelReason { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<EntityEvent> Timeline { get; set; } = new();
}

public record ShopOption(
    string ShopId,
    string Name,
    string HubKey,
    string City,
    bool IsPartner,
    double DistanceMiles,
    decimal EstimatedCost,
    decimal GeneralWorkshopBaseline,
    double EtaHours,
    double Score,
    string Notes);

public record ShopSearchRequest(string Unit, IssueCategory Category, Severity Severity, bool Roadside);

public record AdvisorRequest(
    string Unit,
    IssueCategory Category,
    Severity Severity,
    bool Roadside,
    string Description,
    List<ShopOption> Options,
    decimal ApprovalThreshold);

public record Recommendation(
    string ShopId, string Rationale, string Source, double Confidence, bool RequiresApproval, string? ApprovalReason);

public record ApprovalDecision(bool Approve, string? AlternateShopId, string DecidedBy, string? Note);

public record ShopRequest(
    string RequestId,
    string MaintenanceId,
    string WorkflowId,
    string ShopId,
    string ShopName,
    bool IsPartner,
    string Unit,
    IssueCategory Category,
    Severity Severity,
    bool Roadside,
    decimal Estimate);

public record VendorResponse(string RequestId, string ShopId, bool Accepted, double EtaHours, string? Note);

public record ShopDispatch(
    string Unit, string MaintenanceId, string WorkflowId, string ShopId, string ShopName, double Lat, double Lon, bool Towed);

public record RepairCompletion(string EventId, string RequestId, string ShopId, decimal ActualCost, string Notes);

public record MaintenanceResult(
    string MaintenanceId, string Unit, string Outcome, string? ShopId, bool IsPartner, decimal Cost, decimal Savings);

public record WorkOrderUpsert(
    string ExternalId,
    string Unit,
    string Subject,
    string Description,
    string Priority,
    string Status,
    string? ShopName,
    decimal? EstimatedCost,
    decimal? ActualCost);

public record WorkOrderRef(string Id, string? Number, bool Created);
