using System.Drawing.Drawing2D;

namespace Spectraxia;

internal enum EmitterCategory
{
    Unknown, Light, Small, Large, HighVortex, Heavy, HighPerformance, Rotorcraft,
    Glider, LighterThanAir, Parachute, Ultralight, Uncrewed, Spacecraft
}

internal static class AircraftSymbol
{
    internal static EmitterCategory Decode(int typeCode, int category) => (typeCode, category) switch
    {
        (4,1) => EmitterCategory.Light, (4,2) => EmitterCategory.Small, (4,3) => EmitterCategory.Large,
        (4,4) => EmitterCategory.HighVortex, (4,5) => EmitterCategory.Heavy, (4,6) => EmitterCategory.HighPerformance,
        (4,7) => EmitterCategory.Rotorcraft, (3,1) => EmitterCategory.Glider, (3,2) => EmitterCategory.LighterThanAir,
        (3,3) => EmitterCategory.Parachute, (3,4) => EmitterCategory.Ultralight, (3,6) => EmitterCategory.Uncrewed,
        (3,7) => EmitterCategory.Spacecraft, _ => EmitterCategory.Unknown
    };
    internal static string Label(EmitterCategory c) => c switch
    {
        EmitterCategory.HighVortex => "High wake vortex", EmitterCategory.HighPerformance => "High performance",
        EmitterCategory.LighterThanAir => "Lighter than air", _ => c.ToString()
    };

    // Top views face north in local coordinates. Weight categories describe proportions, not airframes.
    private static readonly PointF[] Light = [new(0,-9),new(1.5f,-7),new(2,-3),new(9,-2),new(9,0),
        new(2,0),new(1,5),new(4,6),new(4,7),new(0,7),new(-4,7),new(-4,6),new(-1,5),new(-2,0),
        new(-9,0),new(-9,-2),new(-2,-3),new(-1.5f,-7)];
    private static readonly PointF[] Transport = [new(0,-11),new(1.8f,-9),new(2,-3),new(11,3),new(11,5),
        new(2,2),new(1.5f,7),new(5,9),new(5,10),new(0,9),new(-5,10),new(-5,9),new(-1.5f,7),
        new(-2,2),new(-11,5),new(-11,3),new(-2,-3),new(-1.8f,-9)];
    private static readonly PointF[] Glider = [new(0,-8),new(1,-2),new(14,0),new(14,1),new(1,0),new(1,6),
        new(4,7),new(0,7),new(-4,7),new(-1,6),new(-1,0),new(-14,1),new(-14,0),new(-1,-2)];
    private static readonly PointF[] Delta = [new(0,-10),new(7,6),new(2,4),new(2,8),new(-2,8),
        new(-2,4),new(-7,6)];
    private static readonly PointF[] Diamond = [new(0,-5),new(5,0),new(0,5),new(-5,0)];

    internal static void Draw(Graphics g, PointF at, EmitterCategory category, double? track, float dpiScale)
    {
        var saved = g.Save();
        try
        {
            g.TranslateTransform(at.X,at.Y);
            g.RotateTransform((float)(track ?? 0));
            g.ScaleTransform(dpiScale,dpiScale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var edge = new Pen(Color.Aquamarine,1.2f) { LineJoin=LineJoin.Round };
            using var fill = new SolidBrush(Color.FromArgb(34,92,92));
            switch (category)
            {
                case EmitterCategory.Rotorcraft:
                    g.FillEllipse(fill,-3,-7,6,11); g.DrawEllipse(edge,-3,-7,6,11);
                    g.DrawLine(edge,0,4,0,10); g.DrawLine(edge,-3,9,2,9);
                    g.DrawLine(edge,-8,-6,8,4); g.DrawLine(edge,-5,7,5,-9); break;
                case EmitterCategory.LighterThanAir:
                    g.FillEllipse(fill,-5,-8,10,14); g.DrawEllipse(edge,-5,-8,10,14);
                    g.DrawLine(edge,-3,8,3,8); break;
                case EmitterCategory.Parachute:
                    g.DrawArc(edge,-7,-7,14,12,180,180); g.DrawLine(edge,-7,-1,7,-1);
                    g.DrawLine(edge,-7,-1,0,8); g.DrawLine(edge,7,-1,0,8); break;
                case EmitterCategory.Uncrewed:
                    g.DrawLine(edge,-5,-5,5,5); g.DrawLine(edge,-5,5,5,-5);
                    foreach (int x in new[]{-5,5}) foreach (int y in new[]{-5,5}) g.DrawEllipse(edge,x-2,y-2,4,4);
                    break;
                default:
                    float size = category switch { EmitterCategory.Small => .79f, EmitterCategory.Heavy => 1.38f,
                        EmitterCategory.HighVortex => 1.1f, _ => 1 };
                    var outline = category switch {
                        EmitterCategory.Light => Light, EmitterCategory.Glider => Glider,
                        EmitterCategory.Small or EmitterCategory.Large or EmitterCategory.HighVortex or EmitterCategory.Heavy => Transport,
                        EmitterCategory.HighPerformance or EmitterCategory.Spacecraft or EmitterCategory.Ultralight => Delta,
                        _ => Diamond };
                    g.ScaleTransform(size,size);
                    g.FillPolygon(fill,outline); g.DrawPolygon(edge,outline);
                    if (category == EmitterCategory.Unknown && track.HasValue) g.DrawLine(edge,0,-5,0,-12);
                    break;
            }
        }
        finally { g.Restore(saved); }
    }
}
