namespace Fleet.Core;

/// <summary>Ranks repair options for a truck. Pure function, so it is easy to unit test.</summary>
public static class ShopFinder
{
    public static List<ShopOption> FindOptions(ShopSearchRequest req, double lat, double lon)
    {
        var maxMiles = req.Roadside ? 250 : req.Severity >= Severity.High ? 320 : 750;
        var riskWeight = req.Severity switch
        {
            Severity.Critical => 12.0,
            Severity.High => 5.0,
            Severity.Medium => 1.5,
            _ => 0.6,
        };

        var all = new List<ShopOption>();
        foreach (var shop in FleetSeed.Shops)
        {
            if (!shop.Capabilities.Contains(req.Category))
            {
                continue;
            }

            var hub = RoadNetwork.Get(shop.HubKey);
            var miles = Math.Round(Geo.DistanceMiles(lat, lon, hub.Lat, hub.Lon) * RoadNetwork.RoadFactor, 0);
            var estimate = CostModel.Estimate(shop, req.Category, req.Roadside, miles);
            var baseline = CostModel.GeneralWorkshopEstimate(req.Category, req.Roadside, miles);
            var operating = req.Roadside ? 0m : (decimal)miles * CostModel.OperatingCostPerMile;
            var score = Math.Round((double)(estimate + operating) + miles * riskWeight, 1);
            var eta = Math.Round(miles / (req.Roadside ? 40.0 : 55.0) + shop.AvgAcceptMinutes / 60.0, 1);
            var notes = shop.IsPartner
                ? $"Partner workshop. About {(1 - (double)CostModel.PartnerRateFactor) * 100:0}% below a typical general workshop invoice."
                : $"General workshop. Labor index {shop.LaborMultiplier:0.00}.";
            all.Add(new ShopOption(shop.ShopId, shop.Name, shop.HubKey, hub.Label, shop.IsPartner, miles, estimate, baseline, eta, score, notes));
        }

        var inRange = all.Where(o => o.DistanceMiles <= maxMiles).OrderBy(o => o.Score).Take(5).ToList();

        // Always keep a partner workshop in the list as a fallback for non-roadside work, even if it is far away.
        if (!req.Roadside && !inRange.Any(o => o.IsPartner))
        {
            var fallback = all.Where(o => o.IsPartner).OrderBy(o => o.DistanceMiles).FirstOrDefault();
            if (fallback != null)
            {
                // Listed last: it is beyond the safe distance for this severity, so it is only used if nobody in range accepts.
                inRange.Add(fallback with
                {
                    Notes = fallback.Notes + " Outside the safe range for this severity; fallback only.",
                    Score = fallback.Score + 100_000,
                });
            }
        }

        if (inRange.Count == 0)
        {
            inRange.AddRange(all.OrderBy(o => o.DistanceMiles).Take(3));
        }

        return inRange;
    }
}
