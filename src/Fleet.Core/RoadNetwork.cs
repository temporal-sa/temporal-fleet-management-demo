using System.Text.Json.Serialization;

namespace Fleet.Core;

public record Hub(string Key, string City, string State, double Lat, double Lon)
{
    [JsonIgnore]
    public GeoPoint Point => new(Lat, Lon);

    [JsonIgnore]
    public string Label => $"{City}, {State}";
}

/// <summary>
/// A simplified interstate network. Trucks in the simulator drive along these corridors and
/// routing uses Dijkstra over them. Static and deterministic, so workflow code may read it.
/// </summary>
public static class RoadNetwork
{
    /// <summary>Distance on the road is longer than great-circle distance.</summary>
    public const double RoadFactor = 1.12;

    public static readonly IReadOnlyList<Hub> Hubs = new List<Hub>
    {
        new("SEA", "Seattle", "WA", 47.61, -122.33),
        new("PDX", "Portland", "OR", 45.52, -122.68),
        new("BOI", "Boise", "ID", 43.62, -116.20),
        new("SAC", "Sacramento", "CA", 38.58, -121.49),
        new("OAK", "Oakland", "CA", 37.80, -122.27),
        new("LAX", "Los Angeles", "CA", 34.05, -118.24),
        new("FON", "Fontana", "CA", 34.09, -117.43),
        new("SAN", "San Diego", "CA", 32.72, -117.16),
        new("LAS", "Las Vegas", "NV", 36.17, -115.14),
        new("PHX", "Phoenix", "AZ", 33.45, -112.07),
        new("TUS", "Tucson", "AZ", 32.22, -110.97),
        new("SLC", "Salt Lake City", "UT", 40.76, -111.89),
        new("CYS", "Cheyenne", "WY", 41.14, -104.82),
        new("DEN", "Denver", "CO", 39.74, -104.99),
        new("ABQ", "Albuquerque", "NM", 35.08, -106.65),
        new("ELP", "El Paso", "TX", 31.76, -106.49),
        new("AMA", "Amarillo", "TX", 35.22, -101.83),
        new("LBF", "North Platte", "NE", 41.12, -100.77),
        new("OMA", "Omaha", "NE", 41.26, -95.93),
        new("DSM", "Des Moines", "IA", 41.59, -93.62),
        new("MSP", "Minneapolis", "MN", 44.98, -93.27),
        new("MKC", "Kansas City", "MO", 39.10, -94.58),
        new("OKC", "Oklahoma City", "OK", 35.47, -97.52),
        new("DFW", "Dallas", "TX", 32.78, -96.80),
        new("SAT", "San Antonio", "TX", 29.42, -98.49),
        new("LRD", "Laredo", "TX", 27.51, -99.51),
        new("HOU", "Houston", "TX", 29.76, -95.37),
        new("LIT", "Little Rock", "AR", 34.75, -92.29),
        new("MSY", "New Orleans", "LA", 29.95, -90.07),
        new("MEM", "Memphis", "TN", 35.15, -90.05),
        new("STL", "St. Louis", "MO", 38.63, -90.20),
        new("CHI", "Chicago", "IL", 41.88, -87.63),
        new("IND", "Indianapolis", "IN", 39.77, -86.16),
        new("DET", "Detroit", "MI", 42.33, -83.05),
        new("SGH", "Springfield", "OH", 39.92, -83.81),
        new("CMH", "Columbus", "OH", 39.96, -83.00),
        new("SDF", "Louisville", "KY", 38.25, -85.76),
        new("BNA", "Nashville", "TN", 36.16, -86.78),
        new("BHM", "Birmingham", "AL", 33.52, -86.80),
        new("ATL", "Atlanta", "GA", 33.75, -84.39),
        new("JAX", "Jacksonville", "FL", 30.33, -81.66),
        new("MCO", "Orlando", "FL", 28.54, -81.38),
        new("CLT", "Charlotte", "NC", 35.23, -80.84),
        new("RIC", "Richmond", "VA", 37.54, -77.44),
        new("PIT", "Pittsburgh", "PA", 40.44, -79.99),
        new("MDT", "Harrisburg", "PA", 40.27, -76.88),
        new("EWR", "Newark", "NJ", 40.74, -74.17),
        new("BOS", "Boston", "MA", 42.36, -71.06),
    };

    /// <summary>Approximate interstate corridors between hubs.</summary>
    public static readonly IReadOnlyList<string[]> Corridors = new List<string[]>
    {
        new[] { "SEA", "PDX" }, new[] { "PDX", "SAC" }, new[] { "SAC", "OAK" }, new[] { "SAC", "LAX" },
        new[] { "LAX", "SAN" }, new[] { "LAX", "FON" }, new[] { "PDX", "BOI" }, new[] { "BOI", "SLC" },
        new[] { "SAC", "SLC" }, new[] { "SLC", "CYS" }, new[] { "CYS", "LBF" }, new[] { "LBF", "OMA" },
        new[] { "OMA", "DSM" }, new[] { "DSM", "CHI" }, new[] { "CHI", "DET" }, new[] { "CHI", "PIT" },
        new[] { "PIT", "MDT" }, new[] { "MDT", "EWR" }, new[] { "EWR", "BOS" }, new[] { "FON", "LAS" },
        new[] { "LAS", "SLC" }, new[] { "FON", "PHX" }, new[] { "PHX", "TUS" }, new[] { "TUS", "ELP" },
        new[] { "ELP", "SAT" }, new[] { "SAT", "HOU" }, new[] { "HOU", "MSY" }, new[] { "MSY", "JAX" },
        new[] { "FON", "ABQ" }, new[] { "ABQ", "AMA" }, new[] { "AMA", "OKC" }, new[] { "OKC", "LIT" },
        new[] { "LIT", "MEM" }, new[] { "MEM", "BNA" }, new[] { "BNA", "CLT" }, new[] { "DEN", "CYS" },
        new[] { "DEN", "ABQ" }, new[] { "ABQ", "ELP" }, new[] { "DEN", "MKC" }, new[] { "MKC", "STL" },
        new[] { "STL", "IND" }, new[] { "IND", "SGH" }, new[] { "SGH", "CMH" }, new[] { "CMH", "PIT" },
        new[] { "LRD", "SAT" }, new[] { "SAT", "DFW" }, new[] { "DFW", "OKC" }, new[] { "OKC", "MKC" },
        new[] { "MKC", "DSM" }, new[] { "DSM", "MSP" }, new[] { "MKC", "OMA" }, new[] { "DFW", "HOU" },
        new[] { "DFW", "ELP" }, new[] { "DFW", "BHM" }, new[] { "BHM", "ATL" }, new[] { "ATL", "CLT" },
        new[] { "CHI", "STL" }, new[] { "STL", "MEM" }, new[] { "MEM", "MSY" }, new[] { "CHI", "IND" },
        new[] { "IND", "SDF" }, new[] { "SDF", "BNA" }, new[] { "BNA", "BHM" }, new[] { "DET", "SGH" },
        new[] { "ATL", "MCO" }, new[] { "JAX", "MCO" }, new[] { "JAX", "RIC" }, new[] { "CLT", "RIC" },
        new[] { "RIC", "EWR" }, new[] { "MSP", "CHI" }, new[] { "PHX", "LAS" }, new[] { "SDF", "CMH" },
        new[] { "ATL", "BNA" }, new[] { "DFW", "LIT" },
    };

    public static readonly IReadOnlyDictionary<string, Hub> ByKey;
    private static readonly Dictionary<string, List<(string To, double Miles)>> Adjacency;

    static RoadNetwork()
    {
        ByKey = Hubs.ToDictionary(h => h.Key);
        Adjacency = Hubs.ToDictionary(h => h.Key, _ => new List<(string, double)>());
        foreach (var c in Corridors)
        {
            var miles = Geo.DistanceMiles(ByKey[c[0]].Point, ByKey[c[1]].Point) * RoadFactor;
            Adjacency[c[0]].Add((c[1], miles));
            Adjacency[c[1]].Add((c[0], miles));
        }
    }

    public static Hub Get(string key) =>
        ByKey.TryGetValue(key, out var hub) ? hub : throw new ArgumentException($"Unknown hub '{key}'");

    public static Hub NearestHub(double lat, double lon) =>
        Hubs.OrderBy(h => Geo.DistanceMiles(lat, lon, h.Lat, h.Lon)).First();

    /// <summary>Dijkstra over the corridor graph. Returns hub keys from origin to destination inclusive.</summary>
    public static List<string> ShortestPath(string fromKey, string toKey)
    {
        if (fromKey == toKey)
        {
            return new List<string> { fromKey };
        }

        var dist = Hubs.ToDictionary(h => h.Key, _ => double.MaxValue);
        var prev = new Dictionary<string, string>();
        var open = new SortedSet<(double Dist, string Key)>();
        dist[fromKey] = 0;
        open.Add((0, fromKey));

        while (open.Count > 0)
        {
            var current = open.Min;
            open.Remove(current);
            if (current.Key == toKey)
            {
                break;
            }

            foreach (var (to, miles) in Adjacency[current.Key])
            {
                var candidate = current.Dist + miles;
                if (candidate < dist[to])
                {
                    open.Remove((dist[to], to));
                    dist[to] = candidate;
                    prev[to] = current.Key;
                    open.Add((candidate, to));
                }
            }
        }

        var path = new List<string>();
        var node = toKey;
        path.Add(node);
        while (prev.TryGetValue(node, out var p))
        {
            node = p;
            path.Add(node);
        }

        path.Reverse();
        return path[0] == fromKey ? path : new List<string> { fromKey, toKey };
    }

    public static double RouteMiles(string fromKey, string toKey) => PathMiles(ShortestPath(fromKey, toKey).Select(k => ByKey[k].Point).ToList());

    /// <summary>Road path between two arbitrary points: start, corridor hubs, end.</summary>
    public static List<GeoPoint> PathBetween(GeoPoint from, GeoPoint to)
    {
        var direct = Geo.DistanceMiles(from, to);
        if (direct < 140)
        {
            return new List<GeoPoint> { from, to };
        }

        var startHub = NearestHub(from.Lat, from.Lon);
        var endHub = NearestHub(to.Lat, to.Lon);
        var points = new List<GeoPoint> { from };
        foreach (var key in ShortestPath(startHub.Key, endHub.Key))
        {
            var p = ByKey[key].Point;
            if (Geo.DistanceMiles(points[^1], p) > 3)
            {
                points.Add(p);
            }
        }

        if (Geo.DistanceMiles(points[^1], to) > 0.5)
        {
            points.Add(to);
        }

        return points;
    }

    public static double PathMiles(IReadOnlyList<GeoPoint> path)
    {
        double total = 0;
        for (var i = 1; i < path.Count; i++)
        {
            total += Geo.DistanceMiles(path[i - 1], path[i]);
        }

        return total * RoadFactor;
    }
}
