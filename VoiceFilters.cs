namespace Spectraxia;

// Operates on the final 48 kHz listening PCM, including both channels of FM.
internal sealed class VoiceFilters
{
    internal static readonly int[] HighPassOptions = [0, 80, 120, 180, 250, 300, 400, 500, 700];
    internal static readonly int[] LowPassOptions = [0, 1800, 2200, 2600, 3200, 3800, 4500, 6000, 9000, 15000];
    private (long Station, ListenMode Mode, int Program, int HighPass, int LowPass, bool Reduce)? last;
    private readonly Biquad high = new(), low = new();
    private readonly NoiseReducer noise = new();

    internal void Apply(short[] pcm, ReceiverSettings settings, long station, int program, bool audible)
    {
        if (!settings.VoiceFilterEnabled || settings.Mode == ListenMode.IQ || !audible)
        {
            last = null;
            return;
        }
        var key = (station, settings.Mode, program, settings.HighPassHz, settings.LowPassHz, settings.ReduceNoise);
        if (last != key)
        {
            high.Configure(settings.HighPassHz, true);
            low.Configure(settings.LowPassHz, false);
            noise.Reset();
            last = key;
        }
        int channels = settings.Mode == ListenMode.WFM ? 2 : 1;
        for (int i = 0; i < pcm.Length; i += channels)
        {
            double left = low.Step(high.Step(pcm[i], 0), 0);
            double right = channels == 2 ? low.Step(high.Step(pcm[i+1], 1), 1) : 0;
            if (settings.ReduceNoise) noise.Apply(ref left, ref right, channels);
            pcm[i] = Sample(left);
            if (channels == 2) pcm[i+1] = Sample(right);
        }
    }

    private static short Sample(double x) => (short)Math.Clamp(Math.Round(x), short.MinValue, short.MaxValue);

    // Second-order Butterworth high- or low-pass. A cutoff of zero bypasses this section.
    private sealed class Biquad
    {
        private double b0, b1, b2, a1, a2;
        private readonly double[] z1 = new double[2], z2 = new double[2];
        private bool bypass;

        internal void Configure(int hz, bool highPass)
        {
            bypass = hz == 0;
            Array.Clear(z1);
            Array.Clear(z2);
            if (bypass) return;
            var (sin, cos) = Math.SinCos(2 * Math.PI * hz / 48000);
            double alpha = sin / Math.Sqrt(2), scale = 1 / (1 + alpha);
            b0 = b2 = (highPass ? 1 + cos : 1 - cos) * scale / 2;
            b1 = (highPass ? -2 : 2) * b0;
            a1 = -2 * cos * scale;
            a2 = (1 - alpha) * scale;
        }

        internal double Step(double x, int channel)
        {
            if (bypass) return x;
            double y = b0*x + z1[channel];
            z1[channel] = b1*x - a1*y + z2[channel];
            z2[channel] = b2*x - a2*y;
            return y;
        }
    }

    // Complementary bands sum back to the input at unity gain. Estimate each noise floor
    // from quiet 10 ms intervals and link stereo gain so voices stay in place.
    private sealed class NoiseReducer
    {
        private static readonly double[] Poles = new[] { 300, 700, 1500, 3000, 6000 }
            .Select(hz => 1 - Math.Exp(-2 * Math.PI * hz / 48000)).ToArray();
        private const int Bands = 6, Block = 480;
        private readonly double[] splitL = new double[Bands-1], splitR = new double[Bands-1];
        private readonly double[] energy = new double[Bands], envelope = new double[Bands],
            minimum = new double[Bands], priorMin = new double[Bands], gain = new double[Bands], target = new double[Bands];
        private int samples, blocks;

        internal void Reset()
        {
            Array.Clear(splitL); Array.Clear(splitR);
            Array.Clear(energy); Array.Clear(envelope);
            Array.Fill(minimum, double.PositiveInfinity); Array.Fill(priorMin, double.PositiveInfinity);
            Array.Fill(gain, 1); Array.Fill(target, 1);
            samples = blocks = 0;
        }

        internal void Apply(ref double left, ref double right, int channels)
        {
            double lastL = 0, lastR = 0, sumL = 0, sumR = 0;
            for (int i = 0; i < Bands; i++)
            {
                double l = left, r = right;
                if (i < Poles.Length)
                {
                    l = splitL[i] += Poles[i] * (left - splitL[i]);
                    r = splitR[i] += Poles[i] * (right - splitR[i]);
                }
                double bandL = l-lastL, bandR = r-lastR;
                lastL = l; lastR = r;
                energy[i] += (bandL*bandL + bandR*bandR) / channels;
                gain[i] += .005 * (target[i] - gain[i]);
                sumL += gain[i] * bandL;
                sumR += gain[i] * bandR;
            }
            left = sumL; right = sumR;
            if (++samples < Block) return;
            samples = 0;
            ++blocks;
            for (int i = 0; i < Bands; i++)
            {
                double power = energy[i] / Block;
                energy[i] = 0;
                envelope[i] = .65 * envelope[i] + .35 * power;
                minimum[i] = Math.Min(minimum[i], envelope[i]);
                double floor = Math.Min(minimum[i], priorMin[i]);
                target[i] = Math.Sqrt(Math.Clamp(1 - floor / Math.Max(1, power), .13, 1));
                if (blocks == 60)
                {
                    priorMin[i] = minimum[i];
                    minimum[i] = double.PositiveInfinity;
                }
            }
            if (blocks == 60) blocks = 0;
        }
    }
}
