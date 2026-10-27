namespace Fleet.Core;

/// <summary>
/// Settings that travel inside workflow inputs. Because they are part of the input (recorded in history),
/// workflow code can use them deterministically.
/// </summary>
public class DemoSettings
{
    public const int DefaultTimeScale = 180;

    /// <summary>Simulated seconds per real second. 180 means one real second is three simulated minutes.</summary>
    public int TimeScale { get; set; } = DefaultTimeScale;

    /// <summary>General workshop repairs estimated above this amount need fleet manager approval.</summary>
    public decimal ApprovalThreshold { get; set; } = 2500m;

    /// <summary>
    /// Upsert the FleetEntity/FleetStatus/UnitNumber/NeedsAttention search attributes. They must be registered on
    /// the server (start-temporal.* does it). The time-skipping test server has none, so tests turn this off.
    /// </summary>
    public bool UseSearchAttributes { get; set; } = true;

    /// <summary>Task queue for Salesforce and TMS activities. Tests point this at their own queue.</summary>
    public string IntegrationsQueue { get; set; } = TaskQueues.Integrations;

    /// <summary>Converts a business duration (in simulated minutes) to the real timer duration.</summary>
    public TimeSpan Real(double simMinutes) =>
        TimeSpan.FromSeconds(Math.Max(2.0, simMinutes * 60.0 / Math.Max(1, TimeScale)));
}
