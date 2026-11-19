namespace Spectraxia;

internal enum AgcSpeed { Off, Fast, Medium, Slow }

// Peak follower at the 48 kHz headphone rate. Both channels share one gain.
internal sealed class AudioAgc
{
    private const double Target = 8192, MaxBoost = 4;
    private static readonly double[] Decay = [1, Release(.12), Release(.45), Release(1.5)];
    private static double Release(double seconds) => Math.Exp(-1 / (48000 * seconds));
    private double peak = Target;

    internal void Reset() => peak = Target;

    internal void Apply(short[] pcm, int channels, AgcSpeed speed)
    {
        if (speed == AgcSpeed.Off) return;
        double decay = Decay[(int)speed];
        for (int i = 0; i < pcm.Length; i += channels)
        {
            int level = Math.Abs((int)pcm[i]);
            if (channels == 2) level = Math.Max(level, Math.Abs((int)pcm[i + 1]));
            // Stop increasing gain near silence. Attack immediately on a loud arrival.
            if (level >= 64) peak = Math.Max(level, Math.Max(Target / MaxBoost, peak * decay));
            double gain = Target / peak;
            pcm[i] = (short)(pcm[i] * gain);
            if (channels == 2) pcm[i + 1] = (short)(pcm[i + 1] * gain);
        }
    }
}
