namespace Fleet.Core;

/// <summary>
/// Illustrative repair economics. Partner workshops cost about 30% of a typical general workshop invoice
/// (negotiated rates), so routing a truck to a partner saves roughly 70% on the repair.
/// </summary>
public static class CostModel
{
    public const decimal PartnerRateFactor = 0.30m;
    public const decimal OperatingCostPerMile = 2.10m;

    public static decimal GeneralWorkshopBaseline(IssueCategory category) => category switch
    {
        IssueCategory.Engine => 3800m,
        IssueCategory.Tires => 1450m,
        IssueCategory.Brakes => 2100m,
        IssueCategory.Aftertreatment => 2900m,
        IssueCategory.Electrical => 950m,
        IssueCategory.PreventiveService => 780m,
        _ => 1000m,
    };

    public static decimal TowCost(double miles) => Math.Round(450m + (decimal)miles * 6.5m, 0);

    public static decimal Estimate(ShopInfo shop, IssueCategory category, bool roadside, double miles)
    {
        var baseline = GeneralWorkshopBaseline(category);
        var labor = shop.IsPartner ? baseline * PartnerRateFactor : baseline * (decimal)shop.LaborMultiplier;
        return Math.Round(labor + (roadside ? TowCost(miles) : 0m), 0);
    }

    /// <summary>What a typical general workshop would have charged for the same job.</summary>
    public static decimal GeneralWorkshopEstimate(IssueCategory category, bool roadside, double miles) =>
        Math.Round(GeneralWorkshopBaseline(category) + (roadside ? TowCost(miles) : 0m), 0);

    /// <summary>Simulated repair duration in hours.</summary>
    public static double RepairSimHours(IssueCategory category) => category switch
    {
        IssueCategory.Engine => 4.0,
        IssueCategory.Tires => 1.0,
        IssueCategory.Brakes => 2.0,
        IssueCategory.Aftertreatment => 3.0,
        IssueCategory.Electrical => 1.5,
        IssueCategory.PreventiveService => 1.5,
        _ => 2.0,
    };
}
