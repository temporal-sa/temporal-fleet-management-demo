using Fleet.Core;
using Fleet.ExternalSystems.Infrastructure;
using Fleet.ExternalSystems.Telematics;

namespace Fleet.ExternalSystems.Vendors;

public class VendorRequestRecord
{
    public ShopRequest Request { get; set; } = null!;
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public RepairCompletion? Completion { get; set; }
}

/// <summary>
/// Partner and general workshops. Partner workshops accept within seconds. General workshops take
/// longer, sometimes decline, and with the "unresponsive vendors" switch never answer, which exercises
/// the workflow's 2-hour (simulated) escalation timer.
/// </summary>
public class VendorPortal
{
    private readonly object gate = new();
    private readonly List<VendorRequestRecord> requests = new();
    private readonly ChaosState chaos;
    private readonly EventLog log;
    private readonly TemporalBridge bridge;
    private readonly TelematicsSimulator simulator;
    private readonly Random rng = new();

    public VendorPortal(ChaosState chaos, EventLog log, TemporalBridge bridge, TelematicsSimulator simulator)
    {
        this.chaos = chaos;
        this.log = log;
        this.bridge = bridge;
        this.simulator = simulator;
    }

    public VendorRequestRecord Request(ShopRequest request)
    {
        VendorRequestRecord record;
        lock (gate)
        {
            var existing = requests.FirstOrDefault(r => r.Request.RequestId == request.RequestId);
            if (existing != null)
            {
                return existing; // Idempotent: a retried activity does not create a second request.
            }

            record = new VendorRequestRecord { Request = request, CreatedAt = DateTime.UtcNow };
            requests.Add(record);
            if (requests.Count > 150)
            {
                requests.RemoveAt(0);
            }
        }

        log.Add("Vendors", "info", $"{request.ShopName} received a {request.Category} request for unit {request.Unit}.", request.WorkflowId);
        _ = Task.Run(async () =>
        {
            var delay = request.IsPartner ? 2 + rng.NextDouble() * 2 : 6 + rng.NextDouble() * 8;
            await Task.Delay(TimeSpan.FromSeconds(delay));
            bool accept;
            string? note = null;
            lock (gate)
            {
                if (record.Status != "Pending")
                {
                    return;
                }

                if (!request.IsPartner && chaos.VendorsUnresponsive)
                {
                    return; // Says nothing. The workflow's timer handles it.
                }

                accept = request.IsPartner || (!chaos.VendorsDecline && rng.NextDouble() < 0.85);
                if (!accept)
                {
                    note = "No bay available today";
                }
            }

            Respond(record.Request.RequestId, accept, note);
        });

        return record;
    }

    public bool Respond(string requestId, bool accepted, string? note)
    {
        VendorRequestRecord? record;
        lock (gate)
        {
            record = requests.FirstOrDefault(r => r.Request.RequestId == requestId);
            if (record == null || record.Status != "Pending")
            {
                return false;
            }

            record.Status = accepted ? "Accepted" : "Declined";
            record.RespondedAt = DateTime.UtcNow;
        }

        var r = record.Request;
        var eta = Math.Round(0.5 + rng.NextDouble() * (r.IsPartner ? 1 : 3), 1);
        bridge.Signal(r.WorkflowId, Signals.VendorResponded, new VendorResponse(r.RequestId, r.ShopId, accepted, eta, note),
            $"{r.ShopName} {(accepted ? "accepted" : "declined")} unit {r.Unit}");
        return true;
    }

    public bool Cancel(string requestId)
    {
        lock (gate)
        {
            var record = requests.FirstOrDefault(r => r.Request.RequestId == requestId);
            if (record == null)
            {
                return true; // Idempotent compensation.
            }

            if (record.Status is "Pending" or "Accepted")
            {
                record.Status = "Cancelled";
                log.Add("Vendors", "info", $"Request to {record.Request.ShopName} for unit {record.Request.Unit} withdrawn.", record.Request.WorkflowId);
            }

            return true;
        }
    }

    public bool Arrived(string requestId)
    {
        VendorRequestRecord? record;
        lock (gate)
        {
            record = requests.FirstOrDefault(r => r.Request.RequestId == requestId);
            if (record == null)
            {
                return false;
            }

            if (record.Status == "InRepair" || record.Status == "Completed")
            {
                return true;
            }

            record.Status = "InRepair";
        }

        var r = record.Request;
        log.Add("Vendors", "info", $"{r.ShopName} started work on unit {r.Unit}.", r.WorkflowId);
        if (!chaos.ManualRepairCompletion)
        {
            var seconds = CostModel.RepairSimHours(r.Category) * 3600 / Math.Max(1, chaos.TimeScale);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, seconds)));
                Complete(requestId);
            });
        }

        return true;
    }

    public bool Complete(string requestId)
    {
        VendorRequestRecord? record;
        RepairCompletion completion;
        lock (gate)
        {
            record = requests.FirstOrDefault(r => r.Request.RequestId == requestId);
            if (record == null || record.Status != "InRepair")
            {
                return false;
            }

            var r = record.Request;
            var cost = Math.Round(r.Estimate * (decimal)(0.92 + rng.NextDouble() * 0.16), 0);
            completion = new RepairCompletion($"{requestId}-complete", requestId, r.ShopId, cost, $"{r.Category} repair complete");
            record.Completion = completion;
            record.Status = "Completed";
        }

        var req = record.Request;
        simulator.MarkRepaired(req.Unit, req.Category);
        bridge.Signal(req.WorkflowId, Signals.RepairCompleted, completion, $"{req.ShopName} finished unit {req.Unit} (${completion.ActualCost:N0})");
        if (chaos.DuplicateEvents)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                bridge.Signal(req.WorkflowId, Signals.RepairCompleted, completion, $"{req.ShopName} sent the completion again (duplicate)");
            });
        }

        return true;
    }

    public bool ResendCompletion(string requestId)
    {
        VendorRequestRecord? record;
        lock (gate)
        {
            record = requests.FirstOrDefault(r => r.Request.RequestId == requestId);
        }

        if (record?.Completion == null)
        {
            return false;
        }

        bridge.Signal(record.Request.WorkflowId, Signals.RepairCompleted, record.Completion,
            $"{record.Request.ShopName} re-sent its completion event (duplicate)");
        return true;
    }

    public List<VendorRequestRecord> Recent()
    {
        lock (gate)
        {
            return requests.AsEnumerable().Reverse().Take(60).ToList();
        }
    }
}

public static class VendorEndpoints
{
    public static void MapVendors(this WebApplication app)
    {
        var portal = app.Services.GetRequiredService<VendorPortal>();

        app.MapPost("/vendors/requests", (ShopRequest request) => Results.Json(portal.Request(request)));
        app.MapGet("/vendors/requests", () => Results.Json(portal.Recent()));
        app.MapPost("/vendors/requests/{id}/cancel", (string id) => portal.Cancel(id) ? Results.Ok() : Results.NotFound());
        app.MapPost("/vendors/requests/{id}/respond", (string id, VendorDecision body) =>
            portal.Respond(id, body.Accepted, body.Note) ? Results.Ok() : Results.Conflict(new { message = "Request is not pending." }));
        app.MapPost("/vendors/requests/{id}/arrived", (string id) => portal.Arrived(id) ? Results.Ok() : Results.NotFound());
        app.MapPost("/vendors/requests/{id}/complete", (string id) =>
            portal.Complete(id) ? Results.Ok() : Results.Conflict(new { message = "Request is not in repair." }));
        app.MapPost("/vendors/requests/{id}/resend-completion", (string id) =>
            portal.ResendCompletion(id) ? Results.Ok() : Results.Conflict(new { message = "No completion to resend." }));
    }

    public record VendorDecision(bool Accepted, string? Note);
}
