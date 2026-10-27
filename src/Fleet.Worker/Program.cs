using Fleet.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Usage:
//   dotnet run --project src/Fleet.Worker                          (all four task queues)
//   dotnet run --project src/Fleet.Worker -- --queues maintenance  (one queue; see scripts/worker.*)
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddHostedService<FleetWorkerService>();
await builder.Build().RunAsync();
