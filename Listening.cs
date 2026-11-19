namespace Spectraxia;

internal enum ListenMode { WFM, AM, NFM, USB, LSB, CW, DSB, IQ }

internal static class Listening
{
    internal static (int Min, int Max, int Increment, int Default, int Step) Range(ListenMode mode) => mode switch
    {
        ListenMode.WFM => (50000, 200000, 5000, 200000, 200000),
        ListenMode.NFM => (6000, 20000, 500, 12500, 5000),
        ListenMode.USB or ListenMode.LSB => (1500, 4500, 100, 2800, 1000),
        ListenMode.CW => (200, 1500, 50, 500, 100),
        ListenMode.IQ => (2000, 36000, 1000, 36000, 1000),
        ListenMode.DSB => (3000, 12000, 500, 6000, 1000),
        _ => (3000, 16000, 500, 10000, 10000)
    };
    internal static (double Low, double High) Edges(ListenMode mode, int width) => mode switch
    {
        ListenMode.USB => (0, width), ListenMode.LSB => (-width, 0), _ => (-width / 2.0, width / 2.0)
    };
    internal static string Name(ListenMode mode) => mode == ListenMode.IQ ? "RAW I/Q" : mode.ToString();
}

// Decimate complex baseband to 48 kHz, then apply a complex FIR with the selected RF edges.
internal sealed class NarrowReceiver
{
    private const int Rate = 48000, Taps = 769;
    private static readonly double[] Decimator = Lowpass(121, 19500.0 / Radio.Rate);
    private readonly double[] inI = new double[242], inQ = new double[242];
    private readonly double[] ringI = new double[2*Taps], ringQ = new double[2*Taps];
    private readonly double[] real = new double[Taps], imag = new double[Taps];
    private int pos, phase, head, beat;
    private double prevI, prevQ, dc, speech;
    internal double Level { get; private set; }

    private static double[] Lowpass(int length, double cutoff)
    {
        var taps = new double[length];
        for (int n = 0; n < length; n++)
        {
            int k = n - length / 2;
            taps[n] = (k == 0 ? 2*cutoff : Math.Sin(2*Math.PI*k*cutoff)/(Math.PI*k))
                * (.42 - .5*Math.Cos(2*Math.PI*n/(length-1)) + .08*Math.Cos(4*Math.PI*n/(length-1)));
        }
        double sum = taps.Sum();
        return taps.Select(x => x / sum).ToArray();
    }
    private readonly ListenMode mode;
    internal NarrowReceiver(ListenMode mode, int width, int initialPhase = 0)
    {
        phase = initialPhase;
        this.mode = mode;
        var (low, high) = Listening.Edges(mode, width);
        var taps = Lowpass(Taps, (high-low)/(2*Rate));
        for (int n = 0; n < Taps; n++)
        {
            double angle = -2*Math.PI*(low+high)/2*(n-Taps/2)/Rate;
            real[n] = taps[n]*Math.Cos(angle);
            imag[n] = taps[n]*Math.Sin(angle);
        }
    }
    private static short Pcm(double x) => (short)Math.Clamp(x, short.MinValue, short.MaxValue);
    internal short[] Decode(byte[] iq)
    {
        var pcm = new short[((iq.Length/2 + phase)/5) * (mode == ListenMode.IQ ? 2 : 1)];
        int written = 0, samples = 0;
        double power = 0;
        for (int n = 0; n < iq.Length; n += 2)
        {
            inI[pos] = inI[pos+121] = iq[n]-127.5;
            inQ[pos] = inQ[pos+121] = iq[n+1]-127.5;
            if (++pos == 121) pos = 0;
            if (++phase < 5) continue;
            phase = 0;
            double i = 0, q = 0;
            for (int k = 0; k < Decimator.Length; k++)
            {
                i += Decimator[k]*inI[pos+k]; q += Decimator[k]*inQ[pos+k];
            }
            ringI[head] = ringI[head+Taps] = i;
            ringQ[head] = ringQ[head+Taps] = q;
            if (++head == Taps) head = 0;
            double fi = 0, fq = 0;
            for (int k = 0; k < Taps; k++)
            {
                double a = ringI[head+k], b = ringQ[head+k];
                fi += real[k]*a - imag[k]*b;
                fq += real[k]*b + imag[k]*a;
            }
            power += fi*fi+fq*fq; samples++;
            double audio = fi*220;
            switch (mode)
            {
                case ListenMode.AM:
                    double envelope = Math.Sqrt(fi*fi+fq*fq);
                    dc += .004*(envelope-dc);
                    audio = (envelope-dc)*350;
                    break;
                case ListenMode.NFM:
                    double deviation = Math.Atan2(prevI*fq-prevQ*fi,prevI*fi+prevQ*fq);
                    prevI = fi; prevQ = fq;
                    dc += .004*(deviation-dc);
                    speech += .32*(deviation-dc-speech);
                    audio = speech*36000;
                    break;
                case ListenMode.CW:
                    double angle = 2*Math.PI*beat/Rate;
                    audio = (fi*Math.Cos(angle)-fq*Math.Sin(angle))*220;
                    beat = (beat+700)%Rate;
                    break;
            }
            pcm[written++] = Pcm(audio);
            if (mode == ListenMode.IQ) pcm[written++] = Pcm(fq*220);
        }
        if (samples > 0) Level = Math.Clamp(10*Math.Log10(power/(samples*127.5*127.5)), -95, 0);
        return pcm;
    }
}
