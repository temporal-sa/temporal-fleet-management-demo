using Fleet.Core;
using Xunit;

namespace Fleet.Tests;

public class ShopFinderTests
{
    [Fact]
    public void MediumSeverityPrefersPartnerWorkshopWhenReachable()
    {
        // Near Kansas City: a general workshop is in town, the Omaha partner workshop is about 190 miles away.
        var kc = RoadNetwork.Get("MKC");
        var options = ShopFinder.FindOptions(new ShopSearchRequest("41209", IssueCategory.Tires, Severity.Medium, false), kc.Lat, kc.Lon);
        var recommendation = MaintenancePolicy.Recommend(new AdvisorRequest(
            "41209", IssueCategory.Tires, Severity.Medium, false, "slow leak", options, 2500m));

        var chosen = options.Single(o => o.ShopId == recommendation.ShopId);
        Assert.True(chosen.IsPartner);
        Assert.False(recommendation.RequiresApproval);
    }

    [Fact]
    public void RoadsideEventPicksTheClosestShopWithoutApproval()
    {
        var amarillo = RoadNetwork.Get("AMA");
        var options = ShopFinder.FindOptions(new ShopSearchRequest("41218", IssueCategory.Engine, Severity.Critical, true), amarillo.Lat, amarillo.Lon);
        var recommendation = MaintenancePolicy.Recommend(new AdvisorRequest(
            "41218", IssueCategory.Engine, Severity.Critical, true, "engine shutdown", options, 2500m));

        Assert.Equal(options.OrderBy(o => o.DistanceMiles).First().ShopId, recommendation.ShopId);
        Assert.False(recommendation.RequiresApproval);
    }

    [Fact]
    public void HighSeverityFarFromPartnerWorkshopsRecommendsGeneralWorkshopWithApproval()
    {
        // Denver: no partner engine workshop within the 320-mile limit for high severity.
        var denver = RoadNetwork.Get("DEN");
        var options = ShopFinder.FindOptions(new ShopSearchRequest("41210", IssueCategory.Engine, Severity.High, false), denver.Lat, denver.Lon);
        var recommendation = MaintenancePolicy.Recommend(new AdvisorRequest(
            "41210", IssueCategory.Engine, Severity.High, false, "coolant high", options, 2500m));

        var chosen = options.Single(o => o.ShopId == recommendation.ShopId);
        Assert.False(chosen.IsPartner);
        Assert.True(recommendation.RequiresApproval);
        Assert.Contains(options, o => o.IsPartner); // the partner fallback is still listed, last
        Assert.True(options.Last().IsPartner);
    }

    [Fact]
    public void ExpensiveGeneralWorkshopRepairNeedsApproval()
    {
        var option = new ShopOption("G-X", "Vendor", "MKC", "Kansas City, MO", false, 20, 3800m, 3800m, 1, 1, "");
        var (required, _) = MaintenancePolicy.ApprovalRule(option, roadside: false, threshold: 2500m);
        Assert.True(required);
    }

    [Fact]
    public void PartnerEstimateIsAboutSeventyPercentBelowGeneralWorkshop()
    {
        var shop = FleetSeed.ShopsById["P-OMA"];
        var estimate = CostModel.Estimate(shop, IssueCategory.Brakes, roadside: false, miles: 10);
        Assert.Equal(CostModel.GeneralWorkshopBaseline(IssueCategory.Brakes) * 0.30m, estimate);
    }
}
