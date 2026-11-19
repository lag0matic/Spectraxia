namespace Spectraxia;

internal sealed class TuningView
{
    internal const long MaxHz = 1_766_000_000;
    private const long ClockHz = 28_800_000;
    // The saved V4 capture recovered with about 200 kHz between the clock and capture edge.
    private static readonly int[] ClockMargins = [200_000, 50_000, 10_000, 1_000];
    internal const int ZoomMaximum = 1000;
    internal static int ZoomSpan(int sampleRate, int position) =>
        (int)Math.Round(sampleRate * Math.Pow(2, -5.0 * position / ZoomMaximum));
    internal long Station { get; private set; }
    internal long CaptureCenter { get; private set; }
    internal long Left { get; private set; }
    internal int Span { get; private set; } = 480_000;
    internal int SampleRate { get; private set; } = Radio.CaptureRate;
    internal ListenMode Mode = ListenMode.WFM;
    internal int FilterHz = 200000;
    internal static bool Fits(long station, long center, int rate, ListenMode mode, int width)
    {
        var edges = Listening.Edges(mode, width);
        long offset = station - center;
        return offset + edges.Low >= -rate / 2 && offset + edges.High <= rate / 2;
    }
    internal void ChangeCapture(int rate)
    {
        SampleRate = Span = rate;
        Tune(Station, true);
    }
    internal void ShowScan(ScanTarget target, int rate)
    {
        SampleRate = Span = rate;
        CaptureCenter = target.Center;
        Station = target.Station;
        Left = CaptureCenter - rate / 2;
    }
    internal long FrequencyAt(double fraction) => Left + (long)Math.Round(fraction * Span);

    internal TuningView(long station)
    {
        Tune(station, true);
    }

    internal void Tune(long hz, bool center, long? pointerOffset = null)
    {
        long prior = Station;
        Station = Math.Clamp(hz, 0, MaxHz);
        if (pointerOffset.HasValue)
            Left = Station - Math.Clamp(pointerOffset.Value, 0, Span);
        else
            Left = center ? Station - Span / 2 : Math.Clamp(Left, Station - Span, Station);
        var (lo, hi) = Listening.Edges(Mode, FilterHz);
        if (Mode == ListenMode.WFM && HdReceiver.InBand(Station))
            (lo, hi) = (-205000, 205000);
        long lower = Math.Max(0, Station + (long)Math.Floor(lo));
        long upper = Math.Min(MaxHz, Station + (long)Math.Ceiling(hi));
        long half = SampleRate / 2;
        bool outside = lower < CaptureCenter - half || upper > CaptureCenter + half;
        if (!outside && !center && pointerOffset == null)
            Left = Math.Clamp(Left, CaptureCenter - half, CaptureCenter + half - Span);
        if (!center && pointerOffset == null && outside && Station != prior)
        {
            // Make room in the direction of travel, while retaining overlap for context.
            long room = Math.Min(SampleRate / 3, Math.Max(FilterHz, SampleRate / 4));
            CaptureCenter = Station > prior ? upper + room - half : lower - room + half;
            if (Span <= half) CaptureCenter = Station;
            CaptureCenter = Math.Clamp(CaptureCenter, half, MaxHz - half);
            Left = Math.Clamp(Left, CaptureCenter - half, CaptureCenter + half - Span);
        }
        Left = Math.Clamp(Left, upper - SampleRate, lower + SampleRate - Span);
        Left = Math.Clamp(Left, 0, MaxHz - Span);
        long low = Math.Max(Left + Span, upper) - half;
        long high = Math.Min(Left, lower) + half;
        CaptureCenter = Math.Clamp(CaptureCenter == 0 ? Station : CaptureCenter, low, high);
        AvoidClockSpur(lower, upper);
    }

    private void AvoidClockSpur(long lower, long upper)
    {
        long gap = Math.Max(ClockHz - upper, lower - ClockHz);
        if (gap <= 0) return; // The wanted passband contains the clock.

        // Leave room on both sides of the capture edge. Use fixed margins so small VFO
        // steps within a tier can share one hardware tuning. Even a 1 Hz gap is feasible.
        long margin = 1;
        foreach (int candidate in ClockMargins)
        {
            if (2L * candidate > gap) continue;
            margin = candidate;
            break;
        }
        long half = SampleRate / 2;
        long safeCenter = upper < ClockHz
            ? Math.Min(CaptureCenter, ClockHz - margin - half)
            : Math.Max(CaptureCenter, ClockHz + margin + half);
        if (safeCenter == CaptureCenter) return;
        CaptureCenter = safeCenter;
        Pan(Left);
    }

    internal void Pan(long desiredLeft)
    {
        long low = Math.Max(0, CaptureCenter - SampleRate / 2);
        long high = Math.Min(MaxHz, CaptureCenter + SampleRate / 2) - Span;
        Left = Math.Clamp(desiredLeft, low, Math.Max(low, high));
    }

    internal void Zoom(int span)
    {
        double anchor = Math.Clamp((Station - Left) / (double)Span, 0, 1);
        long fixedHz = FrequencyAt(anchor);
        Span = span;
        Pan(fixedHz - (long)Math.Round(anchor * span));
    }
}
