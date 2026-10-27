namespace Fleet.Core;

// DTOs exposed by the telematics/ELD simulator. In production this data comes from your
// telematics partner and Kafka topics; Temporal only receives the meaningful business events.

public class TelemetryDto
{
    public double EngineTempF { get; set; }
    public double OilPsi { get; set; }
    public double TirePsi { get; set; }
    public double DefPct { get; set; }
    public double FuelPct { get; set; }
    public double BatteryV { get; set; }
    public int Odometer { get; set; }
    public int PmDueOdometer { get; set; }
}

public class TelematicsTruckDto
{
    public string Unit { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Heading { get; set; }
    public double SpeedMph { get; set; }
    public string State { get; set; } = "Parked";
    public string? DriverId { get; set; }
    public string? DriverDuty { get; set; }
    public HosClock? Hos { get; set; }
    public string? RouteLabel { get; set; }
    public string? NextStop { get; set; }
    public string? RouteWorkflowId { get; set; }
    public bool Towed { get; set; }
    public List<double[]> Path { get; set; } = new();
    public TelemetryDto Telemetry { get; set; } = new();
    public List<string> Faults { get; set; } = new();
}

public class TelematicsDriverDto
{
    public string DriverId { get; set; } = "";
    public string Duty { get; set; } = "OffDuty";
    public HosClock Hos { get; set; } = HosClock.Fresh();
    public string? Unit { get; set; }
}

public class TelematicsFleetDto
{
    public DateTime SimTime { get; set; }
    public int TimeScale { get; set; }
    public List<TelematicsTruckDto> Trucks { get; set; } = new();
    public List<TelematicsDriverDto> Drivers { get; set; } = new();
}
