namespace Fleet.Core;

/// <summary>
/// Deterministic business policy for repair decisions. An AI advisor can recommend a shop,
/// but approval rules always come from policy, not from the model.
/// </summary>
public static class MaintenancePolicy
{
    public static (bool Required, string? Reason) ApprovalRule(ShopOption chosen, bool roadside, decimal threshold)
    {
        if (roadside)
        {
            return (false, null);
        }

        if (!chosen.IsPartner && chosen.EstimatedCost > threshold)
        {
            return (true, $"General workshop repair estimated at ${chosen.EstimatedCost:N0} is above the ${threshold:N0} approval threshold.");
        }

        return (false, null);
    }

    public static Recommendation Recommend(AdvisorRequest req)
    {
        var best = req.Options.OrderBy(o => o.Score).First();
        var nearestGeneral = req.Options.Where(o => !o.IsPartner).OrderBy(o => o.DistanceMiles).FirstOrDefault();
        string rationale;
        if (req.Roadside)
        {
            rationale = $"Roadside event. {best.Name} in {best.City} is the closest capable shop at {best.DistanceMiles:0} miles " +
                        $"(about {best.EtaHours:0.0} h to reach the truck). Speed beats price for a truck that cannot move.";
        }
        else if (best.IsPartner && nearestGeneral != null)
        {
            var savings = nearestGeneral.EstimatedCost - best.EstimatedCost;
            rationale = $"{best.Name} is {best.DistanceMiles:0} miles away. Partner rates bring the estimate to ${best.EstimatedCost:N0}, " +
                        $"${savings:N0} less than {nearestGeneral.Name} ({nearestGeneral.DistanceMiles:0} miles). " +
                        $"Severity {req.Severity} allows the extra distance.";
        }
        else if (!best.IsPartner)
        {
            rationale = $"Severity {req.Severity} limits how far the truck should travel. {best.Name} in {best.City} " +
                        $"({best.DistanceMiles:0} miles) is the best option in range at ${best.EstimatedCost:N0}.";
        }
        else
        {
            rationale = $"{best.Name} is the lowest total cost option at ${best.EstimatedCost:N0}.";
        }

        var (required, reason) = ApprovalRule(best, req.Roadside, req.ApprovalThreshold);
        return new Recommendation(best.ShopId, rationale, "Policy engine", 0.8, required, reason);
    }
}
