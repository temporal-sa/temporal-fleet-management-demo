using Fleet.Core;
using Fleet.ExternalSystems.Infrastructure;

namespace Fleet.ExternalSystems.Telematics;

public enum SimState { Parked, Driving, Dwelling, OnBreak, Broken, Towing, InShop }

public class SimTruck
{
    public string Unit { get; init; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Heading { get; set; }
    public double SpeedMph { get; set; }
    public SimState State { get; set; } = SimState.Parked;
    public string? DriverId { get; set; }
    public List<GeoPoint> Path { get; set; } = new();
    public Queue<DispatchStop> Stops { get; set; } = new();
    public DispatchStop? CurrentStop { get; set; }
    public string? RouteWorkflowId { get; set; }
    public string? RouteLabel { get; set; }
    public bool Towed { get; set; }
    public double DwellRemaining { get; set; }
    public string? Fault { get; set; }
    public double FaultProgress { get; set; }
    public double EngineTempF { get; set; } = 150;
    public double OilPsi { get; set; } = 30;
    public double TirePsi { get; set; } = 105;
    public double DefPct { get; set; } = 82;
    public double FuelPct { get; set; } = 74;
    public double BatteryV { get; set; } = 13.9;
    public double Odometer { get; set; }
    public int PmDueOdometer { get; set; }
    public HashSet<string> SentCodes { get; } = new();
}

public class SimDriver
{
    public string DriverId { get; init; } = "";
    public DutyStatus Duty { get; set; } = DutyStatus.OffDuty;
    public double Drive { get; set; } = 660;
    public double Shift { get; set; } = 840;
    public double Cycle { get; set; } = 4200;
    public double SinceBreak { get; set; }
    public double Rest { get; set; } = 600;
    public double BreakRemaining { get; set; }
    public string? Unit { get; set; }

    public HosClock Hos => new(Math.Round(Drive), Math.Round(Shift), Math.Round(Cycle), Math.Round(SinceBreak));
}

/// <summary>
/// Stand-in for a telematics provider, the ELD, and a stream-processing anomaly job. It moves trucks along the
/// corridor network, runs hours-of-service clocks, fires geofence events, and evaluates simple J1939-style
/// rules on the telemetry stream. Only meaningful business events are signaled to Temporal; the position
/// stream itself never touches a workflow (that belongs in the streaming platform).
/// </summary>
public class TelematicsSimulator : BackgroundService
{
    private const double TickSeconds = 0.5;

    private readonly object gate = new();
    private readonly Dictionary<string, SimTruck> trucks = new();
    private readonly Dictionary<string, SimDriver> drivers = new();
    private readonly ChaosState chaos;
    private readonly EventLog log;
    private readonly TemporalBridge bridge;
    private readonly Random rng = new(41207);
    private DateTime simTime = DateTime.UtcNow;

    public TelematicsSimulator(ChaosState chaos, EventLog log, TemporalBridge bridge)
    {
        this.chaos = chaos;
        this.log = log;
        this.bridge = bridge;
        Seed();
    }

    private void Seed()
    {
        foreach (var d in FleetSeed.Drivers)
        {
            drivers[d.DriverId] = new SimDriver { DriverId = d.DriverId, Unit = d.PairedUnit };
        }

        foreach (var t in FleetSeed.Trucks)
        {
            var hub = RoadNetwork.Get(t.StartHub);
            var driverId = t.PairedDriverId ?? FleetSeed.Drivers.FirstOrDefault(d => d.PairedUnit == t.Unit)?.DriverId;
            trucks[t.Unit] = new SimTruck
            {
                Unit = t.Unit,
                Lat = hub.Lat + (rng.NextDouble() - 0.5) * 0.12,
                Lon = hub.Lon + (rng.NextDouble() - 0.5) * 0.12,
                Heading = rng.NextDouble() * 360,
                DriverId = driverId,
                Odometer = t.Odometer,
                PmDueOdometer = t.PmDueOdometer,
                FuelPct = 45 + rng.NextDouble() * 50,
                DefPct = 40 + rng.NextDouble() * 55,
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TickSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (chaos.SimulationPaused)
            {
                continue;
            }

            try
            {
                Tick(TickSeconds);
            }
            catch (Exception e)
            {
                log.Add("Telematics", "error", $"Simulator tick failed: {e.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ public API (used by endpoints)

    public TelematicsFleetDto Snapshot()
    {
        lock (gate)
        {
            return new TelematicsFleetDto
            {
                SimTime = simTime,
                TimeScale = chaos.TimeScale,
                Trucks = trucks.Values.OrderBy(t => t.Unit, StringComparer.Ordinal).Select(ToDto).ToList(),
                Drivers = drivers.Values.OrderBy(d => d.DriverId, StringComparer.Ordinal).Select(d => new TelematicsDriverDto
                {
                    DriverId = d.DriverId,
                    Duty = d.Duty.ToString(),
                    Hos = d.Hos,
                    Unit = d.Unit,
                }).ToList(),
            };
        }
    }

    public TelematicsTruckDto? Truck(string unit)
    {
        lock (gate)
        {
            return trucks.TryGetValue(unit, out var t) ? ToDto(t) : null;
        }
    }

    public (bool Ok, string Message) Dispatch(DispatchRequest request)
    {
        lock (gate)
        {
            if (!trucks.TryGetValue(request.Unit, out var t))
            {
                return (false, $"Unknown unit {request.Unit}");
            }

            if (t.State == SimState.Broken && !request.Towed)
            {
                return (false, $"Unit {t.Unit} is broken down; only a tow can move it");
            }

            if (request.DriverId != null && request.DriverId != t.DriverId)
            {
                foreach (var other in trucks.Values.Where(o => o.DriverId == request.DriverId))
                {
                    other.DriverId = null;
                }

                t.DriverId = request.DriverId;
                if (!drivers.TryGetValue(request.DriverId, out var newDriver))
                {
                    newDriver = new SimDriver { DriverId = request.DriverId };
                    drivers[request.DriverId] = newDriver;
                }

                newDriver.Unit = t.Unit;
            }

            t.Stops = new Queue<DispatchStop>(request.Stops);
            t.RouteWorkflowId = request.WorkflowId;
            t.RouteLabel = request.RouteLabel;
            t.Towed = request.Towed;
            t.CurrentStop = null;
            BeginLeg(t, DriverOf(t));
            log.Add("Telematics", "info", $"Unit {t.Unit} received route: {request.RouteLabel}.", request.WorkflowId);
            return (true, "ok");
        }
    }

    public bool HoldInPlace(string unit)
    {
        lock (gate)
        {
            if (!trucks.TryGetValue(unit, out var t))
            {
                return false;
            }

            t.Path.Clear();
            t.Stops.Clear();
            t.CurrentStop = null;
            t.SpeedMph = 0;
            if (t.State != SimState.InShop)
            {
                t.State = SimState.Broken;
            }

            var d = DriverOf(t);
            if (d != null)
            {
                SetDuty(t, d, DutyStatus.OnDuty, "waiting with the truck for roadside service");
            }

            return true;
        }
    }

    public (bool Ok, string Message) Inject(string unit, string kind)
    {
        lock (gate)
        {
            if (!trucks.TryGetValue(unit, out var t))
            {
                return (false, $"Unknown unit {unit}");
            }

            t.Fault = kind;
            t.FaultProgress = 0;
            switch (kind)
            {
                case "breakdown":
                    t.OilPsi = 0;
                    StopAtRoadside(t);
                    break;
                case "tire-blowout":
                    t.TirePsi = 24;
                    StopAtRoadside(t);
                    break;
                case "pm-due":
                    t.Odometer = t.PmDueOdometer + 3;
                    t.Fault = null;
                    break;
                case "def-fault":
                    t.DefPct = 3;
                    break;
                case "engine-overheat":
                case "tire-leak":
                case "battery":
                    break;
                default:
                    return (false, $"Unknown fault '{kind}'");
            }

            log.Add("Telematics", "warn", $"Fault injected on unit {unit}: {kind}.");
            return (true, kind);
        }
    }

    public (bool Ok, string Message) InjectSafety(string unit, SafetyEventType type)
    {
        lock (gate)
        {
            if (!trucks.TryGetValue(unit, out var t) || t.DriverId == null)
            {
                return (false, $"Unit {unit} has no driver");
            }

            var speed = t.SpeedMph > 1 ? t.SpeedMph : 57;
            var near = RoadNetwork.NearestHub(t.Lat, t.Lon).Label;
            var evt = new SafetyEvent(
                $"{t.DriverId}-{type}-{simTime:yyyyMMddHHmmss}", t.DriverId, t.Unit, type, Severity.Medium, Math.Round(speed), simTime, near);
            bridge.Signal(WorkflowIds.Driver(t.DriverId), Signals.SafetyEvent, evt,
                $"Camera/ELD event for driver {t.DriverId} on unit {unit}: {type} at {speed:0} mph");
            if (chaos.DuplicateEvents)
            {
                bridge.Signal(WorkflowIds.Driver(t.DriverId), Signals.SafetyEvent, evt, "Same safety event delivered again (duplicate)");
            }

            return (true, t.DriverId);
        }
    }

    public (bool Ok, string Message) ForceBreak(string driverId, double minutes)
    {
        lock (gate)
        {
            var t = trucks.Values.FirstOrDefault(x => x.DriverId == driverId);
            if (t == null || !drivers.TryGetValue(driverId, out var d))
            {
                return (false, $"Driver {driverId} is not in a truck");
            }

            if (t.State == SimState.Driving)
            {
                StartBreak(t, d, DutyStatus.OffDuty, minutes, "pulled into a rest area");
            }
            else
            {
                SetDuty(t, d, DutyStatus.OffDuty, "went off duty");
            }

            return (true, t.Unit);
        }
    }

    public void MarkRepaired(string unit, IssueCategory category)
    {
        lock (gate)
        {
            if (!trucks.TryGetValue(unit, out var t))
            {
                return;
            }

            t.Fault = null;
            t.FaultProgress = 0;
            t.EngineTempF = 180;
            t.OilPsi = 40;
            t.TirePsi = 105;
            t.DefPct = Math.Max(t.DefPct, 90);
            t.BatteryV = 13.9;
            if (category == IssueCategory.PreventiveService || t.Odometer >= t.PmDueOdometer)
            {
                t.PmDueOdometer = (int)t.Odometer + 15_000;
            }

            t.SentCodes.Clear();
            if (t.State is SimState.InShop or SimState.Broken)
            {
                t.State = SimState.Parked;
                t.RouteLabel = null;
            }

            var d = DriverOf(t);
            if (d != null)
            {
                SetDuty(t, d, DutyStatus.OffDuty, "truck repaired");
            }
        }
    }

    // ------------------------------------------------------------------ simulation

    private void Tick(double realSeconds)
    {
        var minutes = realSeconds * chaos.TimeScale / 60.0;
        lock (gate)
        {
            simTime = simTime.AddMinutes(minutes);
            foreach (var t in trucks.Values)
            {
                Step(t, minutes);
                Detect(t);
            }

            Organic(realSeconds);
        }
    }

    private void Step(SimTruck t, double minutes)
    {
        var d = DriverOf(t);
        AdvanceFault(t, minutes);

        switch (t.State)
        {
            case SimState.Driving:
            case SimState.Towing:
                if (t.State == SimState.Driving && d != null)
                {
                    d.Drive -= minutes;
                    d.Shift -= minutes;
                    d.Cycle -= minutes;
                    d.SinceBreak += minutes;
                    if (d.Drive <= 0 || d.Shift <= 0)
                    {
                        StartBreak(t, d, DutyStatus.SleeperBerth, 600, "10-hour reset");
                        break;
                    }

                    if (d.SinceBreak >= 480)
                    {
                        StartBreak(t, d, DutyStatus.OffDuty, 30, "30-minute break");
                        break;
                    }
                }

                t.SpeedMph = t.State == SimState.Towing ? 38 : 58 + rng.NextDouble() * 6;
                Move(t, minutes);
                DrivingTelemetry(t, minutes);
                if (t.Path.Count == 0)
                {
                    Arrive(t, d);
                }

                break;

            case SimState.OnBreak:
                t.SpeedMph = 0;
                if (d == null)
                {
                    t.State = SimState.Driving;
                    break;
                }

                d.BreakRemaining -= minutes;
                if (d.BreakRemaining <= 0)
                {
                    if (d.Duty == DutyStatus.SleeperBerth)
                    {
                        d.Drive = 660;
                        d.Shift = 840;
                    }

                    d.SinceBreak = 0;
                    if (t.Path.Count > 0 || t.CurrentStop != null)
                    {
                        t.State = SimState.Driving;
                        SetDuty(t, d, DutyStatus.Driving, "back on the road");
                    }
                    else
                    {
                        t.State = SimState.Parked;
                    }
                }

                IdleTelemetry(t, minutes);
                break;

            case SimState.Dwelling:
                t.SpeedMph = 0;
                if (d != null)
                {
                    d.Shift -= minutes;
                    d.Cycle -= minutes;
                    d.SinceBreak = 0;
                }

                t.FuelPct = Math.Min(96, t.FuelPct + minutes * 0.5);
                t.DefPct = Math.Min(96, t.DefPct + minutes * 0.3);
                t.DwellRemaining -= minutes;
                IdleTelemetry(t, minutes);
                if (t.DwellRemaining <= 0)
                {
                    Depart(t, d);
                }

                break;

            default:
                t.SpeedMph = 0;
                if (d != null && t.State == SimState.Parked)
                {
                    if (d.Duty is DutyStatus.Driving or DutyStatus.OnDuty)
                    {
                        SetDuty(t, d, DutyStatus.OffDuty, "parked");
                    }

                    d.Rest += minutes;
                    if (d.Rest >= 600 && (d.Drive < 660 || d.Shift < 840))
                    {
                        d.Drive = 660;
                        d.Shift = 840;
                        d.SinceBreak = 0;
                        SignalDuty(t, d, "10 hours off duty; clocks reset");
                    }
                }

                IdleTelemetry(t, minutes);
                break;
        }
    }

    private void Move(SimTruck t, double minutes)
    {
        var remaining = t.SpeedMph * minutes / 60.0;
        double moved = 0;
        while (remaining > 0 && t.Path.Count > 0)
        {
            var target = t.Path[0];
            var distance = Geo.DistanceMiles(t.Lat, t.Lon, target.Lat, target.Lon);
            if (distance < 0.05)
            {
                t.Path.RemoveAt(0);
                continue;
            }

            t.Heading = Geo.BearingDegrees(t.Lat, t.Lon, target.Lat, target.Lon);
            if (distance <= remaining)
            {
                t.Lat = target.Lat;
                t.Lon = target.Lon;
                remaining -= distance;
                moved += distance;
                t.Path.RemoveAt(0);
            }
            else
            {
                var f = remaining / distance;
                t.Lat += (target.Lat - t.Lat) * f;
                t.Lon += (target.Lon - t.Lon) * f;
                moved += remaining;
                remaining = 0;
            }
        }

        t.Odometer += moved * RoadNetwork.RoadFactor;
    }

    private void BeginLeg(SimTruck t, SimDriver? d)
    {
        if (t.Stops.Count == 0)
        {
            t.State = SimState.Parked;
            return;
        }

        var stop = t.Stops.Dequeue();
        t.CurrentStop = stop;
        var path = RoadNetwork.PathBetween(new GeoPoint(t.Lat, t.Lon), new GeoPoint(stop.Lat, stop.Lon));
        path.RemoveAt(0);
        t.Path = path;

        if (t.Towed)
        {
            t.State = SimState.Towing;
            if (d != null)
            {
                SetDuty(t, d, DutyStatus.OnDuty, "riding with the tow");
            }

            return;
        }

        t.State = SimState.Driving;
        if (d == null)
        {
            return;
        }

        if (d.Drive < 60 || d.Shift < 60)
        {
            StartBreak(t, d, DutyStatus.SleeperBerth, Math.Max(30, 600 - d.Rest), "needs a 10-hour reset before driving");
        }
        else
        {
            SetDuty(t, d, DutyStatus.Driving, $"en route to {stop.Name}");
        }
    }

    private void Arrive(SimTruck t, SimDriver? d)
    {
        var stop = t.CurrentStop;
        t.SpeedMph = 0;
        if (stop == null)
        {
            t.State = SimState.Parked;
            return;
        }

        if (stop.ArriveSignal != null && t.RouteWorkflowId != null)
        {
            bridge.Signal(t.RouteWorkflowId, stop.ArriveSignal, Geofence(t, stop), $"Unit {t.Unit} entered geofence: {stop.Name}");
        }

        if (stop.Kind == "shop")
        {
            t.State = SimState.InShop;
            t.Towed = false;
            t.CurrentStop = null;
            t.Stops.Clear();
            t.RouteLabel = $"At {stop.Name}";
            if (d != null)
            {
                SetDuty(t, d, DutyStatus.OffDuty, "truck dropped at the shop");
            }

            return;
        }

        t.State = SimState.Dwelling;
        t.DwellRemaining = stop.DwellMinutes > 0
            ? stop.DwellMinutes
            : stop.Kind == "pickup"
                ? (rng.NextDouble() < 0.15 ? 170 + rng.NextDouble() * 60 : 45 + rng.NextDouble() * 70)
                : 30 + rng.NextDouble() * 50;
        if (d != null)
        {
            SetDuty(t, d, DutyStatus.OnDuty, stop.Kind == "pickup" ? "loading" : "unloading");
        }
    }

    private void Depart(SimTruck t, SimDriver? d)
    {
        var stop = t.CurrentStop!;
        if (stop.DepartSignal != null && t.RouteWorkflowId != null)
        {
            var what = stop.Kind == "pickup" ? "loaded" : "unloaded";
            bridge.Signal(t.RouteWorkflowId, stop.DepartSignal, Geofence(t, stop), $"Unit {t.Unit} {what} at {stop.Name}");
        }

        t.CurrentStop = null;
        if (stop.ParkAfter || t.Stops.Count == 0)
        {
            t.State = SimState.Parked;
            t.RouteLabel = null;
            if (d != null)
            {
                SetDuty(t, d, DutyStatus.OffDuty, "trip complete");
            }

            return;
        }

        BeginLeg(t, d);
    }

    private void StopAtRoadside(SimTruck t)
    {
        t.Path.Clear();
        t.Stops.Clear();
        t.CurrentStop = null;
        t.SpeedMph = 0;
        t.State = SimState.Broken;
        var d = DriverOf(t);
        if (d != null)
        {
            SetDuty(t, d, DutyStatus.OnDuty, "broken down on the shoulder");
        }
    }

    private void StartBreak(SimTruck t, SimDriver d, DutyStatus duty, double minutes, string reason)
    {
        t.State = SimState.OnBreak;
        t.SpeedMph = 0;
        d.BreakRemaining = minutes;
        SetDuty(t, d, duty, reason);
    }

    private void SetDuty(SimTruck t, SimDriver d, DutyStatus duty, string reason)
    {
        if (d.Duty == duty)
        {
            return;
        }

        d.Duty = duty;
        if (duty is DutyStatus.OffDuty or DutyStatus.SleeperBerth)
        {
            d.Rest = 0;
        }

        SignalDuty(t, d, reason);
    }

    private void SignalDuty(SimTruck t, SimDriver d, string reason) =>
        bridge.Signal(
            WorkflowIds.Driver(d.DriverId),
            Signals.DutyStatusChanged,
            new DutyStatusChange(d.DriverId, d.Duty, d.Hos, t.Unit, t.Lat, t.Lon, reason));

    private GeofenceEvent Geofence(SimTruck t, DispatchStop stop) =>
        new($"{t.Unit}-{stop.Kind}-{simTime:yyyyMMddHHmmss}", t.Unit, stop.Name, t.Lat, t.Lon, simTime);

    private SimDriver? DriverOf(SimTruck t) =>
        t.DriverId != null && drivers.TryGetValue(t.DriverId, out var d) ? d : null;

    // ------------------------------------------------------------------ telemetry and anomaly rules

    private void AdvanceFault(SimTruck t, double minutes)
    {
        if (t.Fault == null)
        {
            return;
        }

        t.FaultProgress += minutes;
        switch (t.Fault)
        {
            case "engine-overheat":
                t.EngineTempF = Math.Min(236, 205 + t.FaultProgress * 1.0);
                break;
            case "tire-leak":
                t.TirePsi = Math.Max(76, 104 - t.FaultProgress * 0.7);
                break;
            case "battery":
                t.BatteryV = Math.Max(11.7, 12.9 - t.FaultProgress * 0.03);
                break;
            case "def-fault":
                t.DefPct = 3;
                break;
            case "breakdown":
                t.OilPsi = 0;
                break;
            case "tire-blowout":
                t.TirePsi = 24;
                break;
        }
    }

    private void DrivingTelemetry(SimTruck t, double minutes)
    {
        if (t.Fault != "engine-overheat")
        {
            t.EngineTempF = 194 + rng.NextDouble() * 6;
        }

        if (t.Fault != "breakdown")
        {
            t.OilPsi = 40 + rng.NextDouble() * 8;
        }

        if (t.Fault is null or "engine-overheat" or "def-fault" or "breakdown")
        {
            t.BatteryV = 13.8 + rng.NextDouble() * 0.3;
        }

        if (t.Fault is null || t.Fault == "engine-overheat" || t.Fault == "battery" || t.Fault == "def-fault")
        {
            t.TirePsi = 104 + rng.NextDouble() * 2;
        }

        t.FuelPct = Math.Max(8, t.FuelPct - minutes * 0.04);
        if (t.FuelPct < 20)
        {
            t.FuelPct = 95; // Fuel stop.
        }

        if (t.Fault != "def-fault")
        {
            t.DefPct = Math.Max(12, t.DefPct - minutes * 0.01);
        }
    }

    private void IdleTelemetry(SimTruck t, double minutes)
    {
        if (t.Fault != "engine-overheat")
        {
            t.EngineTempF = Math.Max(110, t.EngineTempF - minutes * 0.8);
        }

        if (t.Fault != "breakdown")
        {
            t.OilPsi = t.State == SimState.Dwelling ? 28 : 0;
        }

        if (t.Fault != "battery")
        {
            t.BatteryV = 12.7;
        }
    }

    private void Detect(SimTruck t)
    {
        if (t.Fault == "breakdown" && t.State == SimState.Broken)
        {
            Emit(t, "SPN100-ENGINE-SHUTDOWN", IssueCategory.Engine, Severity.Critical, "Engine shut down on low oil pressure", t.OilPsi, true);
        }

        if (t.TirePsi <= 40)
        {
            Emit(t, "SPN241-TIRE-BLOWOUT", IssueCategory.Tires, Severity.Critical, $"Tire failure, pressure {t.TirePsi:0} psi", t.TirePsi, true);
        }
        else if (t.TirePsi <= 85)
        {
            Emit(t, "SPN241-TIRE-LOW", IssueCategory.Tires, Severity.Medium, $"Slow leak, steer tire at {t.TirePsi:0} psi", t.TirePsi, false);
        }

        if (t.EngineTempF >= 228)
        {
            Emit(t, "SPN110-COOLANT-HIGH", IssueCategory.Engine, Severity.High, $"Coolant temperature {t.EngineTempF:0}°F and rising", t.EngineTempF, false);
        }

        if (t.DefPct <= 5)
        {
            Emit(t, "SPN1761-DEF-LOW", IssueCategory.Aftertreatment, Severity.Medium, $"DEF level {t.DefPct:0}%, derate risk", t.DefPct, false);
        }

        if (t.BatteryV <= 12.1)
        {
            Emit(t, "SPN168-CHARGING", IssueCategory.Electrical, Severity.Medium, $"Charging voltage {t.BatteryV:0.0} V", t.BatteryV, false);
        }

        if (t.Odometer >= t.PmDueOdometer)
        {
            Emit(t, "PM-A-DUE", IssueCategory.PreventiveService, Severity.Low, $"PM-A service due at {t.PmDueOdometer:N0} mi", t.Odometer, false);
        }
    }

    private void Emit(SimTruck t, string code, IssueCategory category, Severity severity, string description, double value, bool breakdown)
    {
        if (!t.SentCodes.Add(code))
        {
            return; // Already reported for this fault; the repair clears it.
        }

        var anomaly = new Anomaly(
            $"{t.Unit}-{code}-{simTime:yyyyMMddHHmm}", t.Unit, code, category, severity, description,
            Math.Round(value, 1), simTime, breakdown, t.Lat, t.Lon);
        bridge.Signal(WorkflowIds.Truck(t.Unit), Signals.AnomalyDetected, anomaly,
            $"Stream rule {code} fired for unit {t.Unit}: {description}");
    }

    private void Organic(double realSeconds)
    {
        if (chaos.OrganicAnomalies && rng.NextDouble() < realSeconds / 170.0)
        {
            var candidates = trucks.Values.Where(t => t.State == SimState.Driving && t.Fault == null && t.SentCodes.Count == 0).ToList();
            if (candidates.Count > 0)
            {
                var t = candidates[rng.Next(candidates.Count)];
                var kinds = new[] { "tire-leak", "tire-leak", "def-fault", "battery", "engine-overheat" };
                Inject(t.Unit, kinds[rng.Next(kinds.Length)]);
            }
        }

        if (chaos.OrganicSafetyEvents && rng.NextDouble() < realSeconds / 120.0)
        {
            var candidates = trucks.Values.Where(t => t.State == SimState.Driving && t.DriverId != null).ToList();
            if (candidates.Count > 0)
            {
                var t = candidates[rng.Next(candidates.Count)];
                var types = new[] { SafetyEventType.HardBrake, SafetyEventType.FollowingDistance, SafetyEventType.Speeding };
                InjectSafety(t.Unit, types[rng.Next(types.Length)]);
            }
        }
    }

    private TelematicsTruckDto ToDto(SimTruck t)
    {
        var d = DriverOf(t);
        var path = new List<double[]> { new[] { t.Lat, t.Lon } };
        path.AddRange(t.Path.Take(40).Select(p => new[] { p.Lat, p.Lon }));
        foreach (var s in t.Stops)
        {
            path.Add(new[] { s.Lat, s.Lon });
        }

        var faults = new List<string>();
        if (t.Fault != null)
        {
            faults.Add(t.Fault);
        }

        return new TelematicsTruckDto
        {
            Unit = t.Unit,
            Lat = Math.Round(t.Lat, 4),
            Lon = Math.Round(t.Lon, 4),
            Heading = Math.Round(t.Heading),
            SpeedMph = Math.Round(t.SpeedMph),
            State = t.State.ToString(),
            DriverId = t.DriverId,
            DriverDuty = d?.Duty.ToString(),
            Hos = d?.Hos,
            RouteLabel = t.RouteLabel,
            NextStop = t.CurrentStop?.Name,
            RouteWorkflowId = t.RouteWorkflowId,
            Towed = t.Towed,
            Path = t.Path.Count > 0 || t.CurrentStop != null ? path : new List<double[]>(),
            Faults = faults,
            Telemetry = new TelemetryDto
            {
                EngineTempF = Math.Round(t.EngineTempF),
                OilPsi = Math.Round(t.OilPsi),
                TirePsi = Math.Round(t.TirePsi),
                DefPct = Math.Round(t.DefPct),
                FuelPct = Math.Round(t.FuelPct),
                BatteryV = Math.Round(t.BatteryV, 1),
                Odometer = (int)t.Odometer,
                PmDueOdometer = t.PmDueOdometer,
            },
        };
    }
}
