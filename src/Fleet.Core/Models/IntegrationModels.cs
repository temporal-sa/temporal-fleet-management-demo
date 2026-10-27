namespace Fleet.Core;

public record SalesforceEquipment(
    string Id, string Unit, string HomeHub, string Make, string Model, int Year, string Vin, int Odometer, int PmDueOdometer);

public record SalesforceDriver(
    string Id,
    string DriverId,
    string Name,
    string HomeHub,
    string HireStatus,
    List<string> Endorsements,
    string Phone,
    string? PairedUnit);

public record SalesforceFleetSnapshot(List<SalesforceEquipment> Equipment, List<SalesforceDriver> Drivers);

public record TmsEquipmentUpdate(string Unit, string Status, string? LoadId, int Version, string Source);

public record TmsWriteResult(bool Applied, int CurrentVersion, string Note);

public record TmsEquipmentStatus(string Unit, string Status, string? LoadId, int Version, string Source, DateTime UpdatedAt);

public record TmsDriverProfile(string DriverId, string Name, string Status, string HomeHub);

public record DriverOnboardingUpdate(string DriverId, string HireStatus, string? CurrentStep, int StepsComplete, int StepsTotal);

public record EnsureEntitiesResult(List<string> StartedTrucks, List<string> StartedDrivers);

public record SyncConflict(string Unit, string TemporalStatus, string TmsStatus, string Resolution);

public class SyncReport
{
    public DateTime RanAt { get; set; }
    public int EquipmentRecords { get; set; }
    public int DriverRecords { get; set; }
    public List<string> StartedEntities { get; set; } = new();
    public List<SyncConflict> Conflicts { get; set; } = new();
    public List<string> MasterDataUpdates { get; set; } = new();
}
