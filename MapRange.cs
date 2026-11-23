using System.Text.Json;

namespace Spectraxia;

internal sealed record MapRange(GeoPosition? Site = null, bool ShowRings = true)
{
    private const double RadiusNm = 3440.065;
    private const double Radians = Math.PI / 180;
    internal static double Distance(GeoPosition a, GeoPosition b)
    {
        double dLat = (b.Lat-a.Lat)*Radians, dLon = (b.Lon-a.Lon)*Radians;
        double h = Math.Pow(Math.Sin(dLat/2),2) + Math.Cos(a.Lat*Radians)*Math.Cos(b.Lat*Radians)*Math.Pow(Math.Sin(dLon/2),2);
        return 2*RadiusNm*Math.Asin(Math.Sqrt(Math.Clamp(h,0,1)));
    }
    internal static GeoPosition Destination(GeoPosition from, double nm, double bearing)
    {
        double lat = from.Lat*Radians, arc = nm/RadiusNm, angle = bearing*Radians;
        double y = Math.Asin(Math.Clamp(Math.Sin(lat)*Math.Cos(arc)+Math.Cos(lat)*Math.Sin(arc)*Math.Cos(angle),-1,1));
        double x = Math.Atan2(Math.Sin(angle)*Math.Sin(arc)*Math.Cos(lat),Math.Cos(arc)-Math.Sin(lat)*Math.Sin(y));
        return new(y/Radians,Math.IEEERemainder(from.Lon+x/Radians,360));
    }
    internal static MapRange Load(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<MapRange>(File.ReadAllText(path));
        if (value == null || value.Site is { Lat: < -90 or > 90 } or { Lon: < -180 or > 180 })
            throw new InvalidDataException("Invalid map receiver position.");
        return value;
    }
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp,JsonSerializer.Serialize(this));
            File.Move(temp,path,true);
        }
        finally { File.Delete(temp); }
    }
}
