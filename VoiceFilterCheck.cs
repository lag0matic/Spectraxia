namespace Spectraxia;

internal static class VoiceFilterCheck
{
    internal static bool Run()
    {
        var settings = new ReceiverSettings(VoiceFilterEnabled: true, Mode: ListenMode.AM);
        double ToneGain(int hz)
        {
            short[] pcm = Enumerable.Range(0, 4800).Select(i =>
                (short)(8000*Math.Sin(2*Math.PI*hz*i/48000))).ToArray();
            double before = Power(pcm.AsSpan(2400));
            new VoiceFilters().Apply(pcm, settings, 1, -1, true);
            return Math.Sqrt(Power(pcm.AsSpan(2400))/before);
        }
        if (ToneGain(60) > .07 || ToneGain(1000) is < .97 or > 1.01 || ToneGain(8000) > .16) return false;

        var random = new Random(1981);
        var stereo = settings with { Mode=ListenMode.WFM, ReduceNoise=true, HighPassHz=0, LowPassHz=0 };
        var filter = new VoiceFilters();
        double backgroundIn = 0, backgroundOut = 0, speechIn = 0, speechOut = 0;
        for (int block = 0; block < 240; block++)
        {
            var input = new short[960];
            for (int n=0; n<480; n++)
            {
                double tone = block >= 220 ? 6000*Math.Sin(2*Math.PI*1000*n/48000) : 0;
                input[2*n] = (short)(tone + random.Next(-900,901));
            }
            var pcm = (short[])input.Clone();
            filter.Apply(pcm, stereo, 1, 0, true);
            if (pcm.Where((_,i) => (i&1) == 1).Any(x => x != 0)) return false;
            if (block is >= 160 and < 220) { backgroundIn += Power(input); backgroundOut += Power(pcm); }
            if (block >= 225) { speechIn += Power(input); speechOut += Power(pcm); }
        }
        if (backgroundOut/backgroundIn > .45 || speechOut/speechIn < .85) return false;
        foreach (var bypass in new[] { stereo with { VoiceFilterEnabled=false }, stereo with { Mode=ListenMode.IQ } })
        {
            short[] pcm = [123,-456,32767,-32768];
            filter.Apply(pcm,bypass,1,-1,true);
            if (!pcm.SequenceEqual(new short[]{123,-456,32767,-32768})) return false;
        }
        return true;
    }

    private static double Power(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (var sample in samples) sum += (double)sample * sample;
        return sum;
    }
}
