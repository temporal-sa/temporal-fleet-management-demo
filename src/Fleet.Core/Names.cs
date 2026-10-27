using Temporalio.Common;

namespace Fleet.Core;

/// <summary>Task queues. Each one can be served by its own worker process.</summary>
public static class TaskQueues
{
    public const string Entities = "fleet-entities";
    public const string Dispatch = "fleet-dispatch";
    public const string Maintenance = "fleet-maintenance";
    public const string Integrations = "fleet-integrations";

    public static readonly string[] All = { Entities, Dispatch, Maintenance, Integrations };
}

/// <summary>Deterministic workflow IDs. The ID is the business key, which is what makes starts idempotent.</summary>
public static class WorkflowIds
{
    public static string Truck(string unit) => $"truck-{unit}";
    public static string Driver(string driverId) => $"driver-{driverId}";
    public static string Load(string loadId) => $"load-{loadId}";
    public static string Maintenance(string unit, int sequence) => $"maint-{unit}-{sequence:000}";
    public static string Coaching(string driverId, int sequence) => $"coaching-{driverId}-{sequence:000}";

    public const string EquipmentSyncSchedule = "fleet-equipment-sync";
    public const string EquipmentSyncWorkflowPrefix = "fleet-equipment-sync-run";
}

/// <summary>
/// Signal names used by systems outside .NET workflow code (the telematics bridge, vendor portal, IVR).
/// They are explicit so a Kafka consumer or webhook written in any language can send them.
/// </summary>
public static class Signals
{
    public const string AnomalyDetected = "AnomalyDetected";
    public const string ArrivedAtPickup = "ArrivedAtPickup";
    public const string Loaded = "Loaded";
    public const string ArrivedAtDelivery = "ArrivedAtDelivery";
    public const string Unloaded = "Unloaded";
    public const string ArrivedAtShop = "ArrivedAtShop";
    public const string VendorResponded = "VendorResponded";
    public const string RepairCompleted = "RepairCompleted";
    public const string DutyStatusChanged = "DutyStatusChanged";
    public const string SafetyEvent = "SafetyEvent";
    public const string CallAcknowledged = "CallAcknowledged";
    public const string TruckUnavailable = "TruckUnavailable";
    public const string CapacityChanged = "CapacityChanged";
    public const string TruckReleased = "TruckReleased";
    public const string AdditionalAnomaly = "AdditionalAnomaly";
    public const string SyncMasterData = "SyncMasterData";
    public const string RepublishStatus = "RepublishStatus";
}

/// <summary>
/// Custom search attributes. They turn running workflows into a queryable fleet index
/// ("every truck in a shop", "everything waiting on a human") without a separate database.
/// </summary>
public static class FleetSearchAttributes
{
    public static readonly SearchAttributeKey<string> Entity = SearchAttributeKey.CreateKeyword("FleetEntity");
    public static readonly SearchAttributeKey<string> Status = SearchAttributeKey.CreateKeyword("FleetStatus");
    public static readonly SearchAttributeKey<string> Unit = SearchAttributeKey.CreateKeyword("UnitNumber");
    public static readonly SearchAttributeKey<bool> NeedsAttention = SearchAttributeKey.CreateBool("NeedsAttention");

    /// <summary>Name and type pairs, used by the API bootstrapper and the dev-server start scripts.</summary>
    public static readonly (string Name, string Type)[] Definitions =
    {
        ("FleetEntity", "Keyword"),
        ("FleetStatus", "Keyword"),
        ("UnitNumber", "Keyword"),
        ("NeedsAttention", "Bool"),
    };
}
