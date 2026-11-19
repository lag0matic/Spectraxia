namespace Spectraxia;

// At 2 MS/s each half of a Mode S data bit occupies one complex sample.
internal sealed class AdsbDecoder(Action<byte[]> receive, int samplesPerHalfBit = 1)
{
    internal const int Rate = 2_000_000;
    internal const long Frequency = 1_090_000_000;
    private const int Samples = 16 + 112 * 2;
    private readonly byte[] frame = new byte[14];
    private int[] power = [];
    private int retained;
    private int pendingI = -1;

    internal void Feed(ReadOnlySpan<byte> iq)
    {
        int total = retained + (iq.Length + (pendingI >= 0 ? 1 : 0)) / 2;
        if (power.Length < total) Array.Resize(ref power, total + 1024);
        int at = retained, n = 0;
        if (pendingI >= 0 && !iq.IsEmpty)
        {
            power[at++] = Power(pendingI, iq[n++]);
            pendingI = -1;
        }
        for (; n + 1 < iq.Length; n += 2) power[at++] = Power(iq[n], iq[n+1]);
        if (n < iq.Length) pendingI = iq[n];

        int start = 0;
        int frameLength = (Samples - 1) * samplesPerHalfBit + 1;
        while (start + frameLength <= total)
        {
            if (!Preamble(start) || !Decode(start + 16*samplesPerHalfBit)) { start++; continue; }
            receive(frame); // Consumer reads synchronously before the buffer is reused.
            start += frameLength;
        }
        retained = total - start;
        Array.Copy(power, start, power, 0, retained);
    }

    private static int Power(int i, int q)
    {
        i = 2*i - 255;
        q = 2*q - 255;
        return i*i + q*q;
    }

    private bool Preamble(int p)
    {
        int s = samplesPerHalfBit;
        if (power[p] <= power[p+s] || power[p+2*s] <= power[p+3*s] ||
            power[p+7*s] <= power[p+6*s] || power[p+9*s] <= power[p+8*s]) return false;
        int pulses = power[p] + power[p+2*s] + power[p+7*s] + power[p+9*s];
        int quiet = power[p+4*s] + power[p+5*s] + power[p+11*s] + power[p+12*s] + power[p+13*s] + power[p+14*s];
        return pulses > quiet && pulses > 32;
    }

    private bool Decode(int p)
    {
        for (int k = 0; k < frame.Length; k++)
        {
            int value = 0;
            for (int bit = 0; bit < 8; bit++, p += 2*samplesPerHalfBit)
                value = (value << 1) | (power[p] > power[p+samplesPerHalfBit] ? 1 : 0);
            frame[k] = (byte)value;
            if (k == 0 && value >> 3 != 17) return false;
        }
        return ValidFrame(frame);
    }

    internal static bool ValidFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 14 || frame[0] >> 3 != 17) return false;
        int remainder = 0;
        foreach (byte value in frame)
        {
            remainder ^= value << 16;
            for (int bit = 0; bit < 8; bit++)
                remainder = ((remainder << 1) ^ ((remainder & 0x800000) != 0 ? 0xfff409 : 0)) & 0xffffff;
        }
        return remainder == 0;
    }
}
