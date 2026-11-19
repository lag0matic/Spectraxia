namespace Spectraxia;

internal sealed record ManualNotch(long Hz, int WidthHz = 500, bool Enabled = true)
{
    internal static readonly int[] Widths = [100, 500, 2500];
    internal static string WidthName(int hz) => hz switch { 100 => "Narrow · 100 Hz", 500 => "Medium · 500 Hz", _ => "Wide · 2.5 kHz" };
    public override string ToString() => $"{Hz / 1e6:F6} MHz{(Enabled ? "" : " · off")}";
}

// Subtract the output of a complex one-pole resonator at each rejected frequency.
// The state follows the input across USB blocks. Center offsets are signed RF Hz.
internal sealed class RfFilters(int rate)
{
    private sealed class Reject(double offset, double width, int rate)
    {
        private readonly double pole = Math.Exp(-Math.PI * width / rate);
        private readonly double angle = 2 * Math.PI * offset / rate;
        private double stateI, stateQ;

        internal void Apply(float[] samples)
        {
            double re = pole * Math.Cos(angle), im = pole * Math.Sin(angle), feed = 1 - pole;
            double i = stateI, q = stateQ;
            for (int n = 0; n < samples.Length; n += 2)
            {
                float x = samples[n], y = samples[n + 1];
                double nextI = re * i - im * q + feed * x;
                q = im * i + re * q + feed * y;
                i = nextI;
                samples[n] = (float)(x - i);
                samples[n + 1] = (float)(y - q);
            }
            stateI = i;
            stateQ = q;
        }
    }

    private Reject? dc;
    private Reject[] active = [];
    private ManualNotch[]? previous;
    private bool bypass;
    private long lastCenter = -1;
    private float[] buffer = [];

    internal float[]? Process(byte[] iq, long center, ReceiverSettings settings)
    {
        bool recentered = lastCenter != center;
        if (!settings.RemoveDc) dc = null;
        else if (recentered || dc == null) dc = new Reject(0, 10, rate); // 5 Hz half-width
        if (recentered || previous != settings.Notches || bypass != settings.BypassNotches)
        {
            previous = settings.Notches;
            bypass = settings.BypassNotches;
            active = bypass || previous == null ? [] : previous
                .Where(n => n.Enabled && Math.Abs(n.Hz - center) < rate / 2)
                .Select(n => new Reject(n.Hz - center, n.WidthHz, rate)).ToArray();
            lastCenter = center;
        }
        bool trim = settings.BalanceIq && (settings.QGainDb != 0 || settings.QPhaseDegrees != 0);
        if (dc == null && active.Length == 0 && !trim) return null;
        if (buffer.Length != iq.Length) buffer = new float[iq.Length];
        for (int n = 0; n < iq.Length; n++) buffer[n] = iq[n] - 127.5f;
        dc?.Apply(buffer);
        if (trim)
        {
            double angle = (double)settings.QPhaseDegrees * Math.PI / 180;
            double scale = Math.Pow(10, (double)settings.QGainDb / 20) / Math.Cos(angle);
            double leakage = Math.Tan(angle);
            for (int n = 0; n < buffer.Length; n += 2)
                buffer[n+1] = (float)(scale * buffer[n+1] - leakage * buffer[n]);
        }
        foreach (var filter in active) filter.Apply(buffer);
        return buffer;
    }
}
