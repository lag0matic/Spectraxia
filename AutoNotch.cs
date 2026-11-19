using System.Numerics;

namespace Spectraxia;

internal sealed class AutoNotch
{
    private const int Size = 2048, Rate = 48000;
    private static readonly double[] Window = Enumerable.Range(0, Size)
        .Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / Size)).ToArray();
    private readonly Complex[] frame = new Complex[Size];
    private int count, reversed, stable;
    private double candidate, x1, x2, y1, y2, cosine, radius, mix;
    private bool locked;

    private void Detect(int width)
    {
        Spectrum.TransformBitReversed(frame);
        double total = 0, peak = 0;
        int best = 0;
        for (int k = 10; k <= 180; k++)
        {
            double power = frame[k].Magnitude * frame[k].Magnitude;
            total += power;
            if (power > peak) { peak = power; best = k; }
        }
        if (best == 0 || peak < total * .22 || peak < Size * (double)Size * 4)
        {
            stable = 0;
            locked = false;
            return;
        }
        double left = Math.Log(Math.Max(1, frame[best - 1].Magnitude));
        double middle = Math.Log(frame[best].Magnitude);
        double right = Math.Log(Math.Max(1, frame[best + 1].Magnitude));
        double curvature = left - 2 * middle + right;
        double offset = curvature < 0 ? Math.Clamp(.5 * (left - right) / curvature, -.5, .5) : 0;
        double hz = (best + offset) * Rate / Size;
        stable = Math.Abs(hz - candidate) < 24 ? Math.Min(5, stable + 1) : 1;
        candidate = hz;
        locked = stable >= 5;
        if (locked)
        {
            cosine = Math.Cos(2 * Math.PI * hz / Rate);
            radius = Math.Exp(-Math.PI * width / Rate);
        }
    }

    internal void Apply(short[] mono, int width)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            double x = mono[i];
            frame[reversed] = x * Window[count];
            int bit = Size >> 1;
            while (bit > 0 && (reversed & bit) != 0) { reversed ^= bit; bit >>= 1; }
            reversed ^= bit;
            if (++count == Size) { count = 0; Detect(width); }
            double y = (1 + radius) / 2 * (x - 2 * cosine * x1 + x2) + 2 * radius * cosine * y1 - radius * radius * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            mix += .004 * ((locked ? 1 : 0) - mix);
            mono[i] = (short)Math.Clamp(x + mix * (y - x), short.MinValue, short.MaxValue);
        }
    }
}
