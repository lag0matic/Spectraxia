namespace Spectraxia;

internal static class BandPlan
{
    // Selected US listening ranges in kHz, with exclusive upper edges. Group names
    // double as persisted recall keys, including the original amateur band names.
    private sealed record Segment(double Low, double High, string Label, int StartKhz = 0,
        ListenMode Mode = ListenMode.IQ, string? Group = null)
    {
        internal string Key => Group ?? Label;
        internal long LowHz { get; } = (long)Math.Round(Low * 1000);
        internal long HighHz { get; } = (long)Math.Round(High * 1000);
    }

    private const string Military = "Military UHF (shared)";
    private static readonly Segment[] Bands =
    [
        new(535,1705,"AM broadcast",1000,ListenMode.AM),
        new(1800,2000,"160m ham band",1900,ListenMode.LSB),
        new(3500,4000,"80m ham band",3900,ListenMode.LSB),
        new(7000,7300,"40m ham band",7100,ListenMode.LSB),
        new(10100,10150,"30m ham band",10120,ListenMode.CW),
        new(14000,14350,"20m ham band",14230,ListenMode.USB),
        new(18068,18168,"17m ham band",18130,ListenMode.USB),
        new(21000,21450,"15m ham band",21300,ListenMode.USB),
        new(24890,24990,"12m ham band",24950,ListenMode.USB),
        new(26960,27410,"Citizens Band",27185,ListenMode.AM),
        new(28000,29700,"10m ham band",28400,ListenMode.USB),
        new(50000,54000,"6m ham band",50125,ListenMode.USB),
        new(88000,108000,"FM broadcast",101300,ListenMode.WFM),
        new(108000,118000,"Air navigation (VOR / ILS)",113000,ListenMode.AM),
        new(118000,137000,"Civil airband (voice)",122800,ListenMode.AM),
        new(137000,138000,"VHF satellite downlinks",137500),
        new(144000,145800,"2m ham band"),
        new(145800,146000,"2m amateur satellite",Group: "2m ham band"),
        new(146000,148000,"2m ham band",146520,ListenMode.NFM),
        new(162387.5,162562.5,"NOAA Weather Radio",162550,ListenMode.NFM),
        new(222000,225000,"1.25m ham band",223500,ListenMode.NFM),
        new(225000,322000,"Military UHF / aviation / satellite",250000,ListenMode.AM,Military),
        new(322000,328600,"Military UHF / radio astronomy",Group: Military),
        new(328600,335400,"ILS glide slope",332000,ListenMode.AM),
        new(335400,380000,"Military UHF / aviation",Group: Military),
        new(380000,399900,"Federal / military land mobile",390000),
        new(402000,406000,"Weather radiosondes",403000),
        new(406000,406100,"Satellite distress beacons (uplink)",406050),
        new(420000,435000,"70cm ham band"),
        new(435000,438000,"70cm amateur satellite",Group: "70cm ham band"),
        new(438000,450000,"70cm ham band",446000,ListenMode.NFM),
        new(902000,928000,"33cm ham band",906500,ListenMode.NFM),
        // One MHz display windows around the two US ADS-B channel centers.
        new(977500,978500,"Aircraft data (978 UAT)",978000),
        new(1089500,1090500,"Aircraft data (1090 / ADS-B)",1090000),
        new(1240000,1300000,"23cm ham band",1294000,ListenMode.NFM),
        new(1525000,1559000,"Mobile satellite downlinks",1545000),
        new(1559000,1610000,"Satellite navigation (GNSS)",1575420),
        new(1675000,1695000,"L-band weather satellites",1694100)
    ];
    private static readonly int[] CbKhz = [26965,26975,26985,27005,27015,27025,27035,27055,27065,27075,
        27085,27105,27115,27125,27135,27155,27165,27175,27185,27205,27215,27225,27255,27235,27245,
        27265,27275,27285,27295,27305,27315,27325,27335,27345,27355,27365,27375,27385,27395,27405];
    internal static string? At(long hz)
    {
        foreach (var band in Bands)
            if (hz >= band.LowHz && hz < band.HighHz) return band.Key;
        return null;
    }
    private static readonly Dictionary<string, DialSetting> Defaults = Bands.Where(b => b.StartKhz != 0)
        .ToDictionary(b => b.Key, b => new DialSetting(b.StartKhz * 1000L, b.Mode, Listening.Range(b.Mode).Default));
    internal static IEnumerable<string> Choices => Defaults.Keys;
    internal static DialSetting Start(string band) => Defaults[band];

    private static int NearestCbIndex(long hz) =>
        Enumerable.Range(0, CbKhz.Length).MinBy(i => Math.Abs(CbKhz[i]*1000L-hz));

    internal static int? CbChannel(long hz, out long offset)
    {
        int i = NearestCbIndex(hz);
        offset = hz - CbKhz[i]*1000L;
        return Math.Abs(offset) < 5000 ? i+1 : null;
    }

    internal static long Step(long hz, int steps, ListenMode mode)
    {
        if (hz >= 26960000 && hz < 27410000)
        {
            int nearest = NearestCbIndex(hz);
            int target = nearest + steps;
            if (target >= 0 && target < CbKhz.Length) return CbKhz[target]*1000L;
        }
        if (mode == ListenMode.AM && hz is >= 118000000 and < 137000000 && steps != 0)
        {
            const int spacing = 25000;
            long baseHz = steps > 0 ? hz / spacing * spacing : (hz + spacing - 1) / spacing * spacing;
            return baseHz + (long)steps * spacing;
        }
        return hz + (long)steps*Listening.Range(mode).Step;
    }
    internal static void Draw(Graphics g, Font font, long left, int span, int width, int y, long station)
    {
        if (y < 0) return;
        using var fill = new SolidBrush(Color.FromArgb(35, 61, 76));
        foreach (var band in Bands)
        {
            long start = Math.Max(left, band.LowHz), end = Math.Min(left+span, band.HighHz);
            if (start >= end) continue;
            int x = (int)((start-left)*width/span);
            int w = Math.Max(1,(int)((end-start)*width/span));
            var rect = new Rectangle(x,y,w,font.Height);
            g.FillRectangle(fill,rect);
            g.DrawLine(Pens.CadetBlue,x,y,x+w-1,y);
            string label = band.Label;
            if (label == "Citizens Band" && CbChannel(station,out long offset) is int channel)
            {
                string shortLabel = $"Ch {channel}";
                label = offset == 0 ? $"Citizens Band · {shortLabel}" :
                    $"{shortLabel} · {offset/1000.0:+0.###;-0.###} kHz";
                if (TextRenderer.MeasureText(g,label,font).Width > w)
                    label = TextRenderer.MeasureText(g,shortLabel,font).Width <= w ? shortLabel : channel.ToString();
            }
            if (w > 25) TextRenderer.DrawText(g,label,font,rect,Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }
}
