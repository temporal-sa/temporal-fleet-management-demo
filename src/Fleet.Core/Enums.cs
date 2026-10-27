using System.Text.Json.Serialization;

namespace Fleet.Core;

// Enums serialize as strings so Temporal UI histories and API payloads stay readable.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TruckStatus { Available, Reserved, OnLoad, ShopBound, InShop, Breakdown, OutOfService }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverEmployment { Onboarding, Active, Inactive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DutyStatus { OffDuty, SleeperBerth, Driving, OnDuty }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadStatus
{
    Tendered,
    Planning,
    WaitingForCapacity,
    Assigned,
    EnRouteToPickup,
    AtPickup,
    InTransit,
    AtDelivery,
    Delivered,
    Repowering,
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenancePhase
{
    Opened,
    WaitingForLoadCompletion,
    Triage,
    AwaitingApproval,
    Sourcing,
    EnRouteToShop,
    InRepair,
    Completed,
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenanceTrigger { Predictive, Breakdown, Preventive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IssueCategory { Engine, Tires, Brakes, Aftertreatment, Electrical, PreventiveService }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Severity { Low, Medium, High, Critical }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SafetyEventType { HardBrake, Speeding, FollowingDistance, RollStability }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachingStatus { WaitingForStop, Calling, Acknowledged, Escalated, Resolved }
