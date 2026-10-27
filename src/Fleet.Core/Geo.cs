namespace Fleet.Core;

public record GeoPoint(double Lat, double Lon);

public static class Geo
{
    private const double EarthRadiusMiles = 3958.8;

    public static double DistanceMiles(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static double DistanceMiles(GeoPoint a, GeoPoint b) => DistanceMiles(a.Lat, a.Lon, b.Lat, b.Lon);

    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var y = Math.Sin(ToRad(lon2 - lon1)) * Math.Cos(ToRad(lat2));
        var x = Math.Cos(ToRad(lat1)) * Math.Sin(ToRad(lat2)) -
                Math.Sin(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Cos(ToRad(lon2 - lon1));
        return (ToDeg(Math.Atan2(y, x)) + 360) % 360;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
