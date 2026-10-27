namespace Fleet.Core;

public class TruckInput
{
    public string Unit { get; set; } = "";
    public string? SalesforceId { get; set; }
    public string Vin { get; set; } = "";
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public int Year { get; set; }
    public string HomeHub { get; set; } = "";
    public int Odometer { get; set; }
    public int PmDueOdometer { get; set; }
    public DemoSettings Settings { get; set; } = new();

    /// <summary>State carried across continue-as-new.</summary>
    public TruckState? CarryOver { get; set; }
}

public class TruckState
{
    public string Unit { get; set; } = "";
    public TruckStatus Status { get; set; } = TruckStatus.Available;
    public DateTime StatusSince { get; set; }

    /// <summary>Monotonic version of Status. Every write to the TMS carries it, so stale writes are rejected.</summary>
    public int StatusVersion { get; set; }

    public string? CurrentLoadId { get; set; }
    public string? CurrentDriverId { get; set; }
    public string? OpenMaintenanceId { get; set; }
    public bool MaintenanceDeferred { get; set; }
    public int MaintenanceSequence { get; set; }
    public string? SalesforceId { get; set; }
    public string Vin { get; set; } = "";
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public int Year { get; set; }
    public string HomeHub { get; set; } = "";
    public int Odometer { get; set; }
    public int PmDueOdometer { get; set; }
    public int LoadsCompleted { get; set; }
    public decimal MaintenanceSpend { get; set; }
    public int AnomaliesMergedIntoOpenCase { get; set; }
    public int RunNumber { get; set; } = 1;
    public List<EntityEvent> RecentEvents { get; set; } = new();
}

public record ReserveTruckRequest(string LoadId, string? DriverId);

public record ReserveTruckResult(string Unit, string LoadId, int StatusVersion);

public record ReleaseTruckRequest(string LoadId, bool Delivered);

public record MaintenanceHoldRequest(string MaintenanceId, Severity Severity, bool Roadside, string Reason);

public record MaintenanceHoldResult(string? ActiveLoadId, bool Deferred);

public record MaintenanceCompletion(string MaintenanceId, decimal Cost);

public record EquipmentMasterData(
    string? SalesforceId, string HomeHub, string Make, string Model, int Year, string Vin, int PmDueOdometer);

/// <summary>
/// An anomaly detected by the stream processor (the Flink job stand-in). The truck entity decides
/// whether it opens a new maintenance case or merges into the case that is already open.
/// </summary>
public record Anomaly(
    string AnomalyId,
    string Unit,
    string Code,
    IssueCategory Category,
    Severity Severity,
    string Description,
    double Value,
    DateTime DetectedAt,
    bool IsBreakdown,
    double Lat,
    double Lon);

public record TruckStatusSnapshot(string Unit, TruckStatus Status, string? LoadId, int StatusVersion, DateTime StatusSince);
