using System.IO.Compression;
using System.Text.Json;
using System.Drawing.Drawing2D;

namespace Spectraxia;

internal class AircraftMap : Control
{
    private sealed class Boundary(int level, float[] points)
    {
        public int Level { get; } = level;
        public float[] Points { get; } = points;
        internal readonly float South = points.Where((_,i)=>i%2==1).Min();
        internal readonly float North = points.Where((_,i)=>i%2==1).Max();
        internal readonly float West = points.Where((_,i)=>i%2==0).Min();
        internal readonly float East = points.Where((_,i)=>i%2==0).Max();
    }
    private sealed record Place(string Name, double Lat, double Lon, int Rank);
    private sealed record Airport(string Code, string Name, double Lat, double Lon, int Rank, float[] Runways);
    private sealed record County(string Name, double Lat, double Lon);
    private sealed record Geography(Boundary[] Lines, Place[] Cities, Airport[] Airports, County[] Counties);
    private static readonly Lazy<Geography> Map = new(() =>
    {
        using var resource = typeof(AircraftMap).Assembly.GetManifestResourceStream("Spectraxia.AdsbMap")!;
        using var stream = new GZipStream(resource, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Geography>(stream)!;
    });
    private AircraftRow[] rows = [];
    private sealed class Trail(long firstSeen)
    {
        internal long FirstSeen { get; } = firstSeen;
        internal long? LastFix;
        internal readonly Queue<(long Time, GeoPosition Position)> Points = new();
    }
    private const long TrailDurationMs = 10 * 60_000;
    private readonly Dictionary<string, Trail> trails = new();
    private HashSet<string> selected = new();
    private GeoPosition center = new(15, 0);
    private double span = 360;
    private bool positioned;
    private Point drag, press;
    private bool panning, pointerDown;
    internal MapRange Range { get; private set; } = new();
    internal bool PlacingReceiver;
    internal event Action<GeoPosition>? ReceiverPlaced;
    internal event Action<string,bool>? AircraftClicked;
    internal void SetRange(MapRange range)
    {
        Range = range;
        if (!positioned && range.Site is {} site) CenterOn(site);
        else ResetMap();
    }
    private Bitmap? basemap;
    private readonly ToolTip airportTip = new();
    private readonly List<(PointF Point, Airport Airport)> airportPoints = [];
    private Airport? hovered;
    private double ScaleX => Math.Max(1, Width) / span;
    private double ScaleY => ScaleX / Math.Max(.12, Math.Cos(center.Lat*Math.PI/180));

    internal AircraftMap()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(17,29,38);
        AccessibleName = "Aircraft map";
        TabStop = true;
    }
    internal void SelectAircraft(IEnumerable<string> addresses)
    {
        selected = addresses.ToHashSet();
        Invalidate();
    }
    internal void SetAircraft(AircraftRow[] next, long now)
    {
        rows = next;
        var visibleContacts = new HashSet<string>();
        foreach (var row in rows)
        {
            if (row.Position is not {} fix || row.PositionSeen is not long time) continue;
            visibleContacts.Add(row.Address);
            if (!trails.TryGetValue(row.Address, out var trail) || trail.FirstSeen != row.FirstSeen)
                trails[row.Address] = trail = new Trail(row.FirstSeen);
            if (time - trail.LastFix > 60_000) trail.Points.Clear();
            while (trail.Points.TryPeek(out var oldest) && now - oldest.Time > TrailDurationMs)
                trail.Points.Dequeue();
            if (trail.LastFix != time)
            {
                trail.Points.Enqueue((time,fix));
                trail.LastFix = time;
            }
        }
        foreach (var address in trails.Keys.Where(key => !visibleContacts.Contains(key)).ToArray())
            trails.Remove(address);
        if (!positioned && rows.FirstOrDefault(r => r.Position.HasValue)?.Position is {} first) CenterOn(first);
        Invalidate();
    }
    internal void CenterOn(GeoPosition target)
    {
        center = target;
        if (!positioned || span > 20) span = 6;
        positioned = true;
        ResetMap();
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        press = drag = e.Location;
        pointerDown = true;
        panning = false;
        Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Capture || e.Button != MouseButtons.Left)
        {
            Airport? nearest = null;
            float best = 100 * DeviceDpi/96f * DeviceDpi/96f;
            foreach (var (p,airport) in airportPoints)
            {
                float dx=p.X-e.X, dy=p.Y-e.Y, distance=dx*dx+dy*dy;
                if (distance < best) { best=distance; nearest=airport; }
            }
            if (nearest != hovered)
            {
                hovered = nearest;
                airportTip.SetToolTip(this,nearest is null ? null : $"{nearest.Code} · {nearest.Name}");
            }
            return;
        }
        if (!pointerDown || (!panning && InsideClick(e.Location))) return;
        panning = true;
        center = new(Math.Clamp(center.Lat+(e.Y-drag.Y)/ScaleY,-85,85),
            Math.IEEERemainder(center.Lon-(e.X-drag.X)/ScaleX,360));
        positioned = true; drag = e.Location; ResetMap();
    }
    private bool InsideClick(Point point) =>
        Math.Abs(point.X-press.X) <= SystemInformation.DragSize.Width/2 &&
        Math.Abs(point.Y-press.Y) <= SystemInformation.DragSize.Height/2;
    internal string? HitAircraft(Point point)
    {
        double best = Math.Pow(15*DeviceDpi/96.0,2);
        string? match = null;
        foreach (var row in rows)
        {
            if (row.Position is not {} fix) continue;
            var p = Screen(fix.Lon,fix.Lat);
            if (!ClientRectangle.Contains(Point.Round(p))) continue;
            double d = Math.Pow(p.X-point.X,2)+Math.Pow(p.Y-point.Y,2);
            if (d < best || (d == best && selected.Contains(row.Address))) { best = d; match = row.Address; }
        }
        return match;
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) pointerDown = false;
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool click = e.Button == MouseButtons.Left && pointerDown && !panning && InsideClick(e.Location);
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        pointerDown = false;
        Capture = false;
        if (!click) return;
        if (PlacingReceiver)
        {
            var fix = new GeoPosition(Math.Clamp(center.Lat-(e.Y-Height*.5)/ScaleY,-90,90),
                Math.IEEERemainder(center.Lon+(e.X-Width*.5)/ScaleX,360));
            ReceiverPlaced?.Invoke(fix);
        }
        else if (HitAircraft(e.Location) is {} address)
            AircraftClicked?.Invoke(address,ModifierKeys.HasFlag(Keys.Control));
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.Location,e.Delta/120.0);
    }
    internal void ZoomAt(Point mouse,double steps)
    {
        double oldX=ScaleX, oldY=ScaleY;
        double lon = center.Lon+(mouse.X-Width*.5)/oldX, lat = center.Lat-(mouse.Y-Height*.5)/oldY;
        span = Math.Clamp(span * Math.Pow(.78, steps), .035, 360);
        center = new(Math.Clamp(lat+(mouse.Y-Height*.5)/ScaleY,-85,85),
            Math.IEEERemainder(lon-(mouse.X-Width*.5)/ScaleX,360));
        positioned = true; ResetMap();
    }
    protected PointF Screen(double lon, double lat) => new(
        (float)(Width*.5+Math.IEEERemainder(lon-center.Lon,360)*ScaleX),
        (float)(Height*.5-(lat-center.Lat)*ScaleY));

    private void ResetMap()
    {
        basemap?.Dispose(); basemap = null;
        airportPoints?.Clear();
        if (hovered != null) { hovered = null; airportTip.SetToolTip(this,null); }
        Invalidate();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ResetMap(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ResetMap(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ResetMap(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { basemap?.Dispose(); airportTip.Dispose(); }
        base.Dispose(disposing);
    }
    private void DrawBasemap(Graphics g)
    {
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi/96f;
        using var coast = new Pen(Color.FromArgb(84,112,127));
        using var state = new Pen(Color.FromArgb(109,128,140),1.3f*scale);
        using var river = new Pen(Color.FromArgb(49,97,124),scale);
        using var lake = new Pen(Color.FromArgb(53,110,140),scale);
        using var county = new Pen(Color.FromArgb(70,78,87),scale) { DashStyle=DashStyle.Dash };
        using var road = new Pen(Color.FromArgb(87,80,74),1.15f*scale);
        Pen[] pens = [coast,state,river,lake,county,road];
        double halfHeight = Height/(2*ScaleY);
        // Draw state and national borders last where they coincide with county or water lines.
        var lines = Map.Value.Lines;
        for (int iLine=lines.Length-1; iLine>=0; iLine--)
        {
            var line = lines[iLine];
            if (line.Level is 4 or 5 && span > 8 || line.Level is 2 or 3 && span > 30 || line.Level == 1 && span > 45 ||
                line.North < center.Lat-halfHeight || line.South > center.Lat+halfHeight) continue;
            double halfWidth=(line.East-line.West)*.5;
            if (Math.Abs(Math.IEEERemainder(line.West+halfWidth-center.Lon,360)) > span*.5+halfWidth) continue;
            var points = line.Points;
            var prev = Screen(points[0], points[1]);
            for (int i = 2; i < points.Length; i+=2)
            {
                var p = Screen(points[i], points[i+1]);
                if (Math.Abs(p.X-prev.X)<180*ScaleX &&
                    Math.Max(p.X,prev.X)>=0 && Math.Min(p.X,prev.X)<=Width &&
                    Math.Max(p.Y,prev.Y)>=0 && Math.Min(p.Y,prev.Y)<=Height)
                    g.DrawLine(pens[line.Level], prev, p);
                prev = p;
            }
        }
        List<RectangleF> labels = [];
        using var fieldPen = new Pen(Color.FromArgb(222,179,111),1.3f*scale);
        using var minorField = new Pen(Color.FromArgb(153,132,98),scale);
        using var runwayPen = new Pen(Color.FromArgb(247,197,127),2.4f*scale) { StartCap=LineCap.Round, EndCap=LineCap.Round };
        using var fieldText = new SolidBrush(fieldPen.Color);
        bool Label(string text,PointF point,Brush brush)
        {
            var rect = new RectangleF(point,g.MeasureString(text,Font));
            if (labels.Any(r=>r.IntersectsWith(rect))) return false;
            g.DrawString(text,Font,brush,point);
            labels.Add(rect); return true;
        }
        foreach (var airport in Map.Value.Airports)
        {
            if (span > (airport.Rank switch { 0=>36, 1=>10, _=>2.5 })) break;
            if (Math.Abs(airport.Lat-center.Lat)>halfHeight+.1) continue;
            var p=Screen(airport.Lon,airport.Lat);
            if (span <= .8)
            {
                for (int i=0;i<airport.Runways.Length;i+=4)
                {
                    var a=Screen(airport.Runways[i],airport.Runways[i+1]);
                    var b=Screen(airport.Runways[i+2],airport.Runways[i+3]);
                    if (Math.Abs(a.X-b.X)<180*ScaleX && Math.Max(a.X,b.X)>=0 && Math.Min(a.X,b.X)<=Width &&
                        Math.Max(a.Y,b.Y)>=0 && Math.Min(a.Y,b.Y)<=Height) g.DrawLine(runwayPen,a,b);
                }
            }
            if (!ClientRectangle.Contains(Point.Round(p))) continue;
            airportPoints.Add((p,airport));
            bool minor = airport.Rank==2 && span>.85;
            float radius=(airport.Rank==0 ? 4 : minor ? 1.7f : 3)*scale;
            g.DrawEllipse(minor ? minorField : fieldPen,p.X-radius,p.Y-radius,2*radius,2*radius);
            if (!minor) Label(airport.Code,new(p.X+radius+2,p.Y-Font.Height),fieldText);
        }
        void DrawPlaces(bool local)
        {
            if (span >= (local ? 4 : 45)) return;
            foreach (var city in Map.Value.Cities)
            {
                if (!local && city.Rank>=10) break;
                if (local != (city.Rank>=10) || city.Rank>=8 && span>16 || Math.Abs(city.Lat-center.Lat)>halfHeight) continue;
                var p = Screen(city.Lon,city.Lat);
                if (!ClientRectangle.Contains(Point.Round(p))) continue;
                if (!Label(city.Name,new(p.X+4,p.Y-Font.Height*.5f),local ? Brushes.LightSlateGray : Brushes.LightSteelBlue)) continue;
                g.FillEllipse(Brushes.SlateGray,p.X-2,p.Y-2,4,4);
            }
        }
        DrawPlaces(false);
        if (span <= 4)
        foreach (var area in Map.Value.Counties)
        {
            if (Math.Abs(area.Lat-center.Lat)>halfHeight) continue;
            var p=Screen(area.Lon,area.Lat);
            if (!ClientRectangle.Contains(Point.Round(p))) continue;
            float width=g.MeasureString(area.Name,Font).Width;
            Label(area.Name,new(p.X-width/2,p.Y+Font.Height),Brushes.DarkGray);
        }
        DrawPlaces(true);
        if (Range.Site is {} site) DrawRange(g,site);
    }

    private void DrawRange(Graphics g, GeoPosition site)
    {
        using var pen = new Pen(Color.FromArgb(120,106,173,177),DeviceDpi/96f) { DashStyle = DashStyle.Dash };
        if (Range.ShowRings)
        {
            double desiredNm = 105 * DeviceDpi/96.0 * 60 / ScaleY;
            double step = Math.Pow(10, Math.Floor(Math.Log10(desiredNm)));
            step *= desiredNm/step <= 1 ? 1 : desiredNm/step <= 2 ? 2 : desiredNm/step <= 5 ? 5 : 10;
            double middle = MapRange.Distance(site,center);
            double extent = (Width+Height)*60/ScaleY + step;
            int first = (int)Math.Max(1, Math.Floor((middle-extent)/step));
            for (int n = first; n*step <= Math.Min(10800,middle+extent); n++)
            {
                PointF? previous = null;
                PointF? label = null;
                for (int a=45; a<=405; a+=3)
                {
                    var pt = MapRange.Destination(site,n*step,a);
                    var p = Screen(pt.Lon,pt.Lat);
                    if (previous is {} last && Math.Abs(p.X-last.X)<180*ScaleX) g.DrawLine(pen,last,p);
                    if (label == null && p.X>=4 && p.X<Width-70 && p.Y>=0 && p.Y<Height-Font.Height) label=p;
                    previous=p;
                }
                if (label is {} at) TextRenderer.DrawText(g,$"{n*step:0.##} NM",Font,Point.Round(at),Color.LightBlue,BackColor);
            }
        }
        var rx = Screen(site.Lon,site.Lat);
        float size = 5*DeviceDpi/96f;
        g.DrawLine(Pens.PaleTurquoise,rx.X-size,rx.Y,rx.X+size,rx.Y);
        g.DrawLine(Pens.PaleTurquoise,rx.X,rx.Y-size,rx.X,rx.Y+size);
        g.DrawString("RX",Font,Brushes.PaleTurquoise,rx.X+size,rx.Y-Font.Height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width<=0 || Height<=0) return;
        if (basemap==null)
        {
            basemap=new Bitmap(Width,Height);
            basemap.SetResolution(DeviceDpi,DeviceDpi);
            using var background=Graphics.FromImage(basemap);
            DrawBasemap(background);
        }
        var g=e.Graphics;
        g.DrawImageUnscaled(basemap,0,0);
        DrawContacts(g);
    }
    protected void CenterFirst(GeoPosition position)
    {
        if (!positioned) CenterOn(position);
    }
    protected virtual void DrawContacts(Graphics g)
    {
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var row in rows)
        {
            if (!selected.Contains(row.Address) || !trails.TryGetValue(row.Address, out var trail) || trail.Points.Count < 2) continue;
            using var pen = new Pen(Color.FromArgb(205,AircraftSymbol.Tint(row.Category)), 2 * DeviceDpi/96f);
            PointF? previous = null;
            foreach (var (_,fix) in trail.Points)
            {
                var point = Screen(fix.Lon,fix.Lat);
                if (previous is {} start && Math.Abs(point.X-start.X) < 180*ScaleX)
                    g.DrawLine(pen,start,point);
                previous = point;
            }
        }
        g.SmoothingMode = smoothing;
        using var labelBackground = new SolidBrush(Color.FromArgb(224,17,29,38));
        foreach (var row in rows.OrderBy(row => selected.Contains(row.Address)))
        {
            if (row.Position is not {} fix) continue;
            var p = Screen(fix.Lon,fix.Lat);
            if (!ClientRectangle.Contains(Point.Round(p))) continue;
            AircraftSymbol.Draw(g,p,row.Category,row.Track,DeviceDpi/96f);
            string label = row.Callsign.Length>0 ? row.Callsign : row.Address;
            if (selected.Contains(row.Address))
            {
                float radius = 15*DeviceDpi/96f;
                g.DrawEllipse(Pens.PaleTurquoise,p.X-radius,p.Y-radius,2*radius,2*radius);
                if (Range.Site is {} rx) label += $"  {MapRange.Distance(rx,fix):0.0} NM";
            }
            var extent = g.MeasureString(label,Font);
            var at = new PointF(p.X+16*DeviceDpi/96f,p.Y);
            if (at.X+extent.Width>Width) at.X=p.X-16*DeviceDpi/96f-extent.Width;
            at.Y = Math.Max(0,Math.Min(at.Y,Height-extent.Height));
            g.FillRectangle(labelBackground,at.X,at.Y,extent.Width,extent.Height);
            g.DrawString(label,Font,Brushes.WhiteSmoke,at);
        }
        if (!rows.Any(r=>r.Position.HasValue))
            TextRenderer.DrawText(g,"Waiting for an airborne position pair",Font,new Point(8,8),
                Color.WhiteSmoke,BackColor);
    }
}
