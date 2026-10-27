namespace Fleet.Core;

public class LoadInput
{
    public string LoadId { get; set; } = "";
    public string Shipper { get; set; } = "";
    public string Commodity { get; set; } = "";
    public string OriginHub { get; set; } = "";
    public string DestinationHub { get; set; } = "";
    public int WeightLbs { get; set; }
    public bool Hazmat { get; set; }
    public decimal Rate { get; set; }
    public double RouteMiles { get; set; }

    /// <summary>Dispatcher override. Planning tries this unit first, and the truck entity decides whether it can take the load.</summary>
    public string? PreferredUnit { get; set; }

    public DemoSettings Settings { get; set; } = new();
}

public class LoadState
{
    public string LoadId { get; set; } = "";
    public LoadStatus Status { get; set; } = LoadStatus.Tendered;
    public DateTime StatusSince { get; set; }
    public string Shipper { get; set; } = "";
    public string Commodity { get; set; } = "";
    public string OriginHub { get; set; } = "";
    public string DestinationHub { get; set; } = "";
    public bool Hazmat { get; set; }
    public decimal Rate { get; set; }
    public double RouteMiles { get; set; }
    public string? AssignedUnit { get; set; }
    public string? AssignedDriverId { get; set; }
    public string? AssignedDriverName { get; set; }
    public List<string> PreviousUnits { get; set; } = new();
    public int PlanningAttempts { get; set; }
    public int RepowerCount { get; set; }
    public string? RepowerReason { get; set; }
    public GeoPoint? RoadsideTransferPoint { get; set; }
    public bool DetentionStarted { get; set; }
    public bool NeedsAttention { get; set; }
    public DateTime? ArrivedPickupAt { get; set; }
    public DateTime? LoadedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public List<EntityEvent> PlanningLog { get; set; } = new();
    public List<EntityEvent> Timeline { get; set; } = new();
}

public record AssignmentRequest(
    string LoadId,
    double PickupLat,
    double PickupLon,
    bool Hazmat,
    double EstimatedDriveMinutes,
    List<string> ExcludeUnits,
    string? PreferredUnit);

public record AssignmentCandidate(
    string Unit,
    string DriverId,
    string DriverName,
    double DeadheadMiles,
    double DriveMinutesLeft,
    double Score,
    string Rationale,
    bool DispatcherOverride);

public record DispatchStop(
    string Name,
    double Lat,
    double Lon,
    string Kind,
    string? ArriveSignal,
    string? DepartSignal,
    double DwellMinutes,
    bool ParkAfter);

/// <summary>A route sent to the truck's navigation/ELD. Geofence events come back as signals to WorkflowId.</summary>
public record DispatchRequest(
    string Unit,
    string? DriverId,
    string WorkflowId,
    string RouteLabel,
    bool Towed,
    List<DispatchStop> Stops);

public record GeofenceEvent(string EventId, string Unit, string StopName, double Lat, double Lon, DateTime SimTime);

public record TruckUnavailableNotice(string Unit, string MaintenanceId, string Reason, double Lat, double Lon);

public record LoadMilestone(string LoadId, string Milestone, string? Unit, string? DriverId, string IdempotencyKey);

public record DownstreamEvent(string Topic, string Key, string PayloadJson);

public record LoadResult(string LoadId, string Outcome, string? Unit, int Repowers, bool Detention);
