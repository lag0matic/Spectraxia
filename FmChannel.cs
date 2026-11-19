namespace Spectraxia;

// Move one station to baseband, low-pass its I/Q, then decimate to the WFM input rate.
internal sealed class FmChannel(int sampleRate = Radio.CaptureRate)
{
    private const int Taps = 97;
    private readonly double[][] filters = Enumerable.Range(0, 31).Select(i => MakeFilter(50 + i * 5, sampleRate)).ToArray();
    private readonly double[] iRing = new double[2 * Taps], qRing = new double[2 * Taps];
    private int position, phase;
    private double oscI = 1, oscQ;
    private double stepI = 1, stepQ;
    private long? offset;
    private byte[] output = [];
    internal double LevelDbfs { get; private set; } = -95;
    internal void ResetFilter() => offset = null;

    private static double[] MakeFilter(int bandwidthKhz, int sampleRate)
    {
        double fc = bandwidthKhz * 500.0 / sampleRate;
        var taps = Enumerable.Range(0, Taps).Select(j =>
        {
            int k = j - Taps / 2;
            return (k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k))
                * (0.54 - 0.46 * Math.Cos(2 * Math.PI * j / (Taps - 1)));
        }).ToArray();
        double sum = taps.Sum();
        return taps.Select(t => t / sum).ToArray();
    }

    internal byte[] Extract(byte[] iq, long offsetHz, int bandwidthKhz = 200, float[]? filtered = null)
    {
        if (offset != offsetHz)
        {
            offset = offsetHz;
            (stepQ, stepI) = Math.SinCos(-2 * Math.PI * offsetHz / sampleRate);
            Array.Clear(iRing);
            Array.Clear(qRing);
        }
        int factor = sampleRate / Radio.Rate;
        int bytes = 2 * ((iq.Length / 2 + phase) / factor);
        if (output.Length != bytes) output = new byte[bytes];
        int written = 0;
        var filter = filters[(bandwidthKhz - 50) / 5];
        double power = 0;
        for (int n = 0; n + 1 < iq.Length; n += 2)
        {
            double i = filtered == null ? iq[n] - 127.5 : filtered[n];
            double q = filtered == null ? iq[n + 1] - 127.5 : filtered[n + 1];
            iRing[position] = iRing[position + Taps] = i * oscI - q * oscQ;
            qRing[position] = qRing[position + Taps] = i * oscQ + q * oscI;
            (oscI, oscQ) = (oscI * stepI - oscQ * stepQ, oscQ * stepI + oscI * stepQ);
            if (++position == Taps) position = 0;
            if (++phase != factor) continue;
            phase = 0;
            double fi = 0, fq = 0;
            for (int k = 0; k < Taps; k++)
            {
                fi += filter[k] * iRing[position + k];
                fq += filter[k] * qRing[position + k];
            }
            power += fi * fi + fq * fq;
            output[written++] = (byte)Math.Clamp(Math.Round(fi + 127.5), 0, 255);
            output[written++] = (byte)Math.Clamp(Math.Round(fq + 127.5), 0, 255);
        }
        double norm = Math.Sqrt(oscI * oscI + oscQ * oscQ);
        oscI /= norm;
        oscQ /= norm;
        if (written > 0)
            LevelDbfs = Math.Clamp(10 * Math.Log10(power / (written / 2 * 127.5 * 127.5)), -95, 0);
        return output;
    }
}
