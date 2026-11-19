namespace Spectraxia;

internal static class HdCheck
{
    internal static int Replay(string path, int rate, long offset, int program, bool paced)
    {
        if (program is < 0 or > 7 || rate < 744188) return 2;
        using var file = File.OpenRead(path);
        var reception = new HdReception(100_000_000 + offset, 100_000_000);
        using var decoder = paced ? null : new HdDecoder(rate, reception);
        using var live = paced ? new HdReceiver(rate) : null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        byte[] iq = new byte[32768];
        long pairs = 0, audible = 0, played = 0;
        int seen = 0, peak = 0;
        bool selected = false;
        while (file.Position < file.Length)
        {
            if (file.Length - file.Position < iq.Length) Array.Resize(ref iq, (int)(file.Length - file.Position));
            file.ReadExactly(iq);
            if (live == null) decoder!.Feed(iq);
            else
            {
                double delay = pairs * 1000.0 / rate - clock.Elapsed.TotalMilliseconds;
                if (delay >= 1) Thread.Sleep((int)delay);
                var result = live.Push((byte[])iq.Clone(), reception.Station, reception.Center, true, selected ? program : -1);
                if (result == null) return 5;
                reception = result;
            }
            seen |= reception.Services;
            int frames = (int)(((pairs + iq.Length / 2) * 48000 / rate) - pairs * 48000 / rate);
            pairs += iq.Length / 2;
            var pcm = new short[frames * 2];
            if (!selected)
            {
                Array.Fill(pcm, (short)77);
                if (reception.Render(pcm, true, false) || pcm.Any(x => x != 77)) return 3;
                if ((reception.Services & (1 << program)) != 0)
                {
                    reception.Select(program);
                    selected = true;
                }
            }
            else if (reception.Render(pcm, true, false))
            {
                played += frames;
                foreach (short value in pcm)
                {
                    int amplitude = Math.Abs((int)value);
                    peak = Math.Max(peak, amplitude);
                    if (amplitude > 32) audible++;
                }
            }
        }
        reception.Select(-1);
        short[] analog = [111, -222];
        if (reception.Render(analog, true, false) || analog[0] != 111 || analog[1] != -222) return 4;
        if (live != null)
        {
            if (live.Push(iq, reception.Station, reception.Center, false, -1) != null) return 6;
            var fresh = live.Push(iq, reception.Station + 200000, reception.Station + 200000, true, -1);
            if (fresh == null || fresh == reception || fresh.Services != 0 || fresh.Render(analog, true, false)) return 7;
        }
        Console.WriteLine($"Station {reception.Name}, detected mask 0x{seen:X2}, HD{program + 1}, rendered {played / 48000.0:F2} s, peak {peak}, non-silent samples {audible}");
        return played > 48000 && audible > 1000 ? 0 : 1;
    }
}
