using Fleet.Core;

namespace Fleet.ExternalSystems.Telematics;

public static class TelematicsEndpoints
{
    public record InjectRequest(string Unit, string Kind);

    public record SafetyRequest(string Unit, SafetyEventType? Type);

    public record BreakRequest(double? Minutes);

    public static void MapTelematics(this WebApplication app)
    {
        var sim = app.Services.GetRequiredService<TelematicsSimulator>();

        app.MapGet("/telematics/fleet", () => Results.Json(sim.Snapshot()));

        app.MapGet("/telematics/trucks/{unit}", (string unit) =>
        {
            var truck = sim.Truck(unit);
            return truck == null ? Results.NotFound() : Results.Json(truck);
        });

        app.MapPost("/telematics/dispatch", (DispatchRequest request) =>
        {
            var (ok, message) = sim.Dispatch(request);
            return ok ? Results.Ok() : Results.Conflict(new { message });
        });

        app.MapPost("/telematics/hold/{unit}", (string unit) => sim.HoldInPlace(unit) ? Results.Ok() : Results.NotFound());

        app.MapPost("/telematics/inject", (InjectRequest request) =>
        {
            var (ok, message) = sim.Inject(request.Unit, request.Kind);
            return ok ? Results.Json(new { message }) : Results.BadRequest(new { message });
        });

        app.MapPost("/telematics/inject-safety", (SafetyRequest request) =>
        {
            var (ok, message) = sim.InjectSafety(request.Unit, request.Type ?? SafetyEventType.HardBrake);
            return ok ? Results.Json(new { driverId = message }) : Results.BadRequest(new { message });
        });

        app.MapPost("/telematics/drivers/{driverId}/break", (string driverId, BreakRequest? request) =>
        {
            var (ok, message) = sim.ForceBreak(driverId, request?.Minutes ?? 30);
            return ok ? Results.Json(new { unit = message }) : Results.BadRequest(new { message });
        });
    }
}
