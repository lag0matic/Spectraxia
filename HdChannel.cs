namespace Spectraxia;

// Shift the station to DC, then resample to nrsc5's 744187.5 complex samples/s.
internal sealed class HdChannel
{
    private const int Taps = 48, Phases = 128, TwiceOutputRate = 1_488_375;
    private readonly int rate;
    private readonly float[][] kernel = new float[Phases][];
    private readonly float[] real = new float[Taps * 2], imag = new float[Taps * 2];
    private readonly double cosStep, sinStep;
    private double oscI = 1, oscQ;
    private int cursor, phase;
    private short[] samples = [];

    internal HdChannel(int rate, long offset)
    {
        this.rate = rate;
        (sinStep, cosStep) = Math.SinCos(-2 * Math.PI * offset / rate);
        double cutoff = 275000.0 / rate;
        for (int p = 0; p < Phases; p++)
        {
            var taps = kernel[p] = new float[Taps];
            for (int k = 0; k < Taps; k++)
            {
                double x = k - (Taps - 1) * .5 + p / (double)Phases;
                double window = .42 - .5 * Math.Cos(2 * Math.PI * k / (Taps - 1)) + .08 * Math.Cos(4 * Math.PI * k / (Taps - 1));
                taps[k] = (float)(window * (Math.Abs(x) < 1e-10 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x)));
            }
            float scale = 128 / taps.Sum();
            for (int k = 0; k < Taps; k++) taps[k] *= scale;
        }
    }

    internal (short[] Samples, int Count) Convert(byte[] iq)
    {
        int capacity = 2 * (int)(2 + (long)(iq.Length / 2) * TwiceOutputRate / (2 * rate));
        if (samples.Length < capacity) samples = new short[capacity];
        int count = 0;
        for (int n = 0; n < iq.Length; n += 2)
        {
            double i = iq[n] - 127.5, q = iq[n + 1] - 127.5;
            real[cursor] = real[cursor + Taps] = (float)(i * oscI - q * oscQ);
            imag[cursor] = imag[cursor + Taps] = (float)(q * oscI + i * oscQ);
            (oscI, oscQ) = (oscI * cosStep - oscQ * sinStep, oscQ * cosStep + oscI * sinStep);
            if (++cursor == Taps) cursor = 0;
            phase += TwiceOutputRate;
            if (phase < 2 * rate) continue;
            phase -= 2 * rate;
            var taps = kernel[(int)((long)phase * Phases / TwiceOutputRate)];
            float re = 0, im = 0;
            for (int k = 0; k < Taps; k++)
            {
                re += real[cursor + k] * taps[k];
                im += imag[cursor + k] * taps[k];
            }
            samples[count++] = (short)Math.Clamp(re, short.MinValue, short.MaxValue);
            samples[count++] = (short)Math.Clamp(im, short.MinValue, short.MaxValue);
        }
        double norm = Math.Sqrt(oscI * oscI + oscQ * oscQ);
        oscI /= norm;
        oscQ /= norm;
        return (samples, count);
    }
}
