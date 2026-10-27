namespace Fleet.ExternalSystems.Infrastructure;

/// <summary>Failure switches for the demo scenarios. Flip them from the fleet UI.</summary>
public class ChaosState
{
    public bool TmsOutage { get; set; }
    public bool SalesforceOutage { get; set; }
    public bool SalesforceRateLimit { get; set; }
    public bool VendorsUnresponsive { get; set; }
    public bool VendorsDecline { get; set; }
    public bool DuplicateEvents { get; set; }
    public bool ManualRepairCompletion { get; set; }
    public bool IvrNoAnswer { get; set; }
    public bool OrganicAnomalies { get; set; } = true;
    public bool OrganicSafetyEvents { get; set; } = true;
    public bool SimulationPaused { get; set; }
    public int TimeScale { get; set; } = 180;
}

public class ChaosPatch
{
    public bool? TmsOutage { get; set; }
    public bool? SalesforceOutage { get; set; }
    public bool? SalesforceRateLimit { get; set; }
    public bool? VendorsUnresponsive { get; set; }
    public bool? VendorsDecline { get; set; }
    public bool? DuplicateEvents { get; set; }
    public bool? ManualRepairCompletion { get; set; }
    public bool? IvrNoAnswer { get; set; }
    public bool? OrganicAnomalies { get; set; }
    public bool? OrganicSafetyEvents { get; set; }
    public bool? SimulationPaused { get; set; }

    public List<string> ApplyTo(ChaosState s)
    {
        var changes = new List<string>();
        void Set(bool? value, bool current, Action<bool> apply, string label)
        {
            if (value.HasValue && value.Value != current)
            {
                apply(value.Value);
                changes.Add($"{label} {(value.Value ? "on" : "off")}");
            }
        }

        Set(TmsOutage, s.TmsOutage, v => s.TmsOutage = v, "TMS outage");
        Set(SalesforceOutage, s.SalesforceOutage, v => s.SalesforceOutage = v, "Salesforce outage");
        Set(SalesforceRateLimit, s.SalesforceRateLimit, v => s.SalesforceRateLimit = v, "Salesforce rate limiting");
        Set(VendorsUnresponsive, s.VendorsUnresponsive, v => s.VendorsUnresponsive = v, "Unresponsive vendors");
        Set(VendorsDecline, s.VendorsDecline, v => s.VendorsDecline = v, "Vendors declining");
        Set(DuplicateEvents, s.DuplicateEvents, v => s.DuplicateEvents = v, "Duplicate events");
        Set(ManualRepairCompletion, s.ManualRepairCompletion, v => s.ManualRepairCompletion = v, "Manual repair completion");
        Set(IvrNoAnswer, s.IvrNoAnswer, v => s.IvrNoAnswer = v, "Drivers not answering calls");
        Set(OrganicAnomalies, s.OrganicAnomalies, v => s.OrganicAnomalies = v, "Background anomalies");
        Set(OrganicSafetyEvents, s.OrganicSafetyEvents, v => s.OrganicSafetyEvents = v, "Background safety events");
        Set(SimulationPaused, s.SimulationPaused, v => s.SimulationPaused = v, "Simulation paused");
        return changes;
    }
}
