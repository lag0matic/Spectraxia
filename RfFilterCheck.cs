namespace Spectraxia;

internal static class RfFilterCheck
{
    internal static bool Run()
    {
        const int rate = 960000;
        const long station = 101300000;
        // A weak +50 Hz CW signal beside a much stronger constant I/Q offset.
        var off = new ReceiverAudio(rate);
        var on = new ReceiverAudio(rate);
        var settings = new ReceiverSettings(Mode: ListenMode.CW);
        var corrected = settings with { RemoveDc = true };
        var iq = new byte[19200]; // 10 ms
        double dcBefore = 0, dcAfter = 0;
        double beforeCos = 0, beforeSin = 0, afterCos = 0, afterSin = 0;
        for (int b = 0; b < 60; b++)
        {
            for (int n = 0; n < iq.Length / 2; n++)
            {
                double t = (b * (iq.Length / 2) + n) / (double)rate;
                var (s, c) = Math.SinCos(2 * Math.PI * 50 * t);
                iq[2*n] = (byte)Math.Round(127.5 + 32 + 4*c);
                iq[2*n+1] = (byte)Math.Round(127.5 + 4*s);
            }
            var original = off.Decode(iq, station, station, settings, 100);
            var filtered = on.Decode(iq, station, station, corrected, 100);
            if (b < 40) continue;
            for (int i = 0; i < iq.Length; i += 2)
            {
                dcBefore += iq[i] - 127.5;
                dcAfter += on.FilteredIq![i];
            }
            for (int i = 0; i < filtered.Length; i++)
            {
                var (s, c) = Math.SinCos(2 * Math.PI * 750 * (b * filtered.Length + i) / 48000);
                beforeCos += c * original[i]; beforeSin += s * original[i];
                afterCos += c * filtered[i]; afterSin += s * filtered[i];
            }
        }
        double cwRatio = Math.Sqrt((afterCos*afterCos + afterSin*afterSin) / (beforeCos*beforeCos + beforeSin*beforeSin));
        if (Math.Abs(dcAfter / dcBefore) > .01 || !(cwRatio is > .94 and < 1.06)) return false;

        // The same saved RF carrier moves from above to below capture center.
        var bank = new RfFilters(rate);
        foreach (int width in ManualNotch.Widths)
        foreach (int offset in new[] { 24000, -24000 })
        {
            var config = settings with { Notches = [new(station, width)] };
            long center = station - offset;
            double rejected = 0, wanted = 0;
            for (int b = 0; b < 12; b++)
            {
                for (int n = 0; n < iq.Length / 2; n++)
                {
                    double angle = 2*Math.PI*(b*(iq.Length/2)+n)/rate;
                    var (s,c) = Math.SinCos(offset*angle);
                    var (s2,c2) = Math.SinCos((offset+6000)*angle);
                    iq[2*n] = (byte)Math.Round(127.5 + 64*c + 16*c2);
                    iq[2*n+1] = (byte)Math.Round(127.5 + 64*s + 16*s2);
                }
                var samples = bank.Process(iq, center, config)!;
                if (b != 11) continue;
                double Projection(int hz)
                {
                    double re = 0, im = 0;
                    for (int n = 0; n < samples.Length/2; n++)
                    {
                        var (s,c) = Math.SinCos(2*Math.PI*hz*n/rate);
                        re += samples[2*n]*c + samples[2*n+1]*s;
                        im += samples[2*n+1]*c - samples[2*n]*s;
                    }
                    return Math.Sqrt(re*re + im*im) / (samples.Length/2);
                }
                rejected = Projection(offset);
                wanted = Projection(offset+6000);
            }
            if (rejected > .64 || wanted is < 15 or > 17 ||
                bank.Process(iq, center, config with { BypassNotches = true }) != null ||
                bank.Process(iq, center, config with { Notches = [new(station, width, false)] }) != null ||
                bank.Process(iq, center + rate, config) != null) return false;
        }
        // Known gain/phase error makes an image at the opposite FFT bin.
        var imageIq = new byte[2*Spectrum.Bins];
        const int bin = 137;
        for (int i = 0; i < Spectrum.Bins; i++)
        {
            double angle = 2*Math.PI*bin*i/Spectrum.Bins;
            imageIq[2*i] = (byte)Math.Round(127.5 + 85*Math.Cos(angle));
            imageIq[2*i+1] = (byte)Math.Round(127.5 + 85*Math.Pow(10,1.5/20)*Math.Sin(angle+4*Math.PI/180));
        }
        var skew = settings with { BalanceIq = true, QGainDb = -1.5m, QPhaseDegrees = 4m };
        var untrimmed = Spectrum.Analyze(imageIq);
        var balanced = Spectrum.Analyze(imageIq, bank.Process(imageIq, station, skew));
        int mirror = Spectrum.Bins/2-bin, carrier = Spectrum.Bins/2+bin;
        return untrimmed[mirror]-balanced[mirror] > 35 && Math.Abs(balanced[carrier]-20*Math.Log10(85/127.5)) < .1 &&
            bank.Process(imageIq,station,skew with { BalanceIq = false }) == null;
    }
}
