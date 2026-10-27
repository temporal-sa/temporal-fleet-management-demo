using Fleet.Core;
using Fleet.Workflows;

namespace Fleet.Api.Services;

public record TenderRequest(string? OriginHub, string? DestinationHub, bool? Hazmat, string? PreferredUnit);

/// <summary>Creates loads the way a customer tender would arrive: one LoadWorkflow per load.</summary>
public class LoadTenderer
{
    private readonly TemporalClientProvider temporal;
    private readonly FleetStateCache cache;
    private readonly ExternalSystemsProxy external;
    private readonly DemoSettings settings;
    private readonly Random rng = new();

    public LoadTenderer(TemporalClientProvider temporal, FleetStateCache cache, ExternalSystemsProxy external, IConfiguration config)
    {
        this.temporal = temporal;
        this.cache = cache;
        this.external = external;
        settings = new DemoSettings
        {
            TimeScale = config.GetValue("Demo:TimeScale", DemoSettings.DefaultTimeScale),
            ApprovalThreshold = config.GetValue("Demo:ApprovalThreshold", 2500m),
        };
    }

    public async Task<LoadInput> TenderAsync(TenderRequest request)
    {
        var client = await temporal.GetAsync();
        var snapshot = cache.Current;

        string origin;
        if (request.OriginHub != null)
        {
            origin = request.OriginHub;
        }
        else
        {
            var anchor = request.PreferredUnit != null
                ? snapshot.Trucks.FirstOrDefault(t => t.Unit == request.PreferredUnit)
                : RandomItem(snapshot.Trucks.Where(t => t.Entity?.Status == TruckStatus.Available && t.Telematics != null).ToList());
            origin = anchor?.Telematics != null
                ? RoadNetwork.NearestHub(anchor.Telematics.Lat, anchor.Telematics.Lon).Key
                : RoadNetwork.Hubs[rng.Next(RoadNetwork.Hubs.Count)].Key;
        }

        var destination = request.DestinationHub ?? PickDestination(origin);
        var shipper = FleetSeed.Shippers[rng.Next(FleetSeed.Shippers.Length)];
        var hazmat = request.Hazmat ?? shipper.Hazmat;
        var miles = Math.Round(RoadNetwork.RouteMiles(origin, destination), 0);
        var loadId = $"L{rng.Next(100_000, 999_999)}";

        var input = new LoadInput
        {
            LoadId = loadId,
            Shipper = shipper.Shipper,
            Commodity = shipper.Commodity,
            OriginHub = origin,
            DestinationHub = destination,
            WeightLbs = rng.Next(18_000, 44_000),
            Hazmat = hazmat,
            Rate = Math.Round((decimal)miles * 2.85m + 150m, 0),
            RouteMiles = miles,
            PreferredUnit = request.PreferredUnit,
            Settings = settings,
        };

        await client.StartWorkflowAsync(
            (LoadWorkflow wf) => wf.RunAsync(input),
            new(id: WorkflowIds.Load(loadId), taskQueue: TaskQueues.Dispatch));

        var o = RoadNetwork.Get(origin);
        var d = RoadNetwork.Get(destination);
        var note = request.PreferredUnit != null ? $" Dispatcher asked for unit {request.PreferredUnit}." : "";
        await external.LogAsync("Fleet API", "info", $"Load {loadId} tendered: {o.Label} to {d.Label}, {miles:0} mi.{note}", WorkflowIds.Load(loadId));
        return input;
    }

    private string PickDestination(string origin)
    {
        for (var i = 0; i < 40; i++)
        {
            var candidate = RoadNetwork.Hubs[rng.Next(RoadNetwork.Hubs.Count)].Key;
            if (candidate == origin)
            {
                continue;
            }

            var miles = RoadNetwork.RouteMiles(origin, candidate);
            if (miles is >= 350 and <= 1100)
            {
                return candidate;
            }
        }

        return origin == "OMA" ? "DFW" : "OMA";
    }

    private T? RandomItem<T>(List<T> items)
        where T : class =>
        items.Count == 0 ? null : items[rng.Next(items.Count)];
}

/// <summary>Keeps freight flowing during the demo by tendering a load now and then.</summary>
public class AutoTenderService : BackgroundService
{
    private readonly LoadTenderer tenderer;
    private readonly FleetStateCache cache;
    private readonly DemoControls controls;
    private readonly IConfiguration config;
    private readonly ILogger<AutoTenderService> log;

    public AutoTenderService(LoadTenderer tenderer, FleetStateCache cache, DemoControls controls, IConfiguration config, ILogger<AutoTenderService> log)
    {
        this.tenderer = tenderer;
        this.cache = cache;
        this.controls = controls;
        this.config = config;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Demo:AutoTenderSeconds", 35));
        var target = config.GetValue("Demo:TargetActiveLoads", 12);
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = cache.Current;
                if (controls.AutoTender && snapshot.TemporalConnected && snapshot.Kpis.ActiveLoads < target && snapshot.Trucks.Count > 0)
                {
                    await tenderer.TenderAsync(new TenderRequest(null, null, null, null));
                }
            }
            catch (Exception e)
            {
                log.LogWarning("Auto tender failed: {Message}", e.Message);
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
