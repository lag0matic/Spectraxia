using System.Security.Cryptography;

namespace Spectraxia;

internal static class RecordingCheck
{
    internal static bool AutoNotchWorks()
    {
        foreach (int width in new[] { 40, 100, 220 })
        {
            var filter = new AutoNotch();
            double phase = 0, rejected = 0, rejectedQuadrature = 0, wanted = 0, reference = 0;
            for (int b = 0; b < 160; b++)
            {
                var pcm = new short[600];
                var whistle = new double[pcm.Length];
                var quadrature = new double[pcm.Length];
                var comparison = new double[pcm.Length];
                for (int i = 0; i < pcm.Length; i++)
                {
                    double t = (b * pcm.Length + i) / 48000.0;
                    phase += 2 * Math.PI * (1100 + 12 * t) / 48000;
                    whistle[i] = Math.Sin(phase);
                    quadrature[i] = Math.Cos(phase);
                    comparison[i] = Math.Sin(2 * Math.PI * 481 * t);
                    pcm[i] = (short)(10000 * whistle[i] + 2600 * comparison[i] + 1200 * Math.Sin(2 * Math.PI * (270 * t + 60 * t * t)));
                }
                filter.Apply(pcm, width);
                if (b < 80) continue;
                for (int i = 0; i < pcm.Length; i++)
                {
                    rejected += pcm[i] * whistle[i]; rejectedQuadrature += pcm[i] * quadrature[i];
                    wanted += pcm[i] * comparison[i]; reference += comparison[i] * comparison[i];
                }
            }
            if (Math.Sqrt(rejected * rejected + rejectedQuadrature * rejectedQuadrature) / reference > 1000 ||
                wanted / reference is < 2280 or > 2820) return false;
            short[] burst = Enumerable.Range(0, 4800).Select(i => (short)(5000 * Math.Sin(i * .15))).ToArray();
            var copy = burst.ToArray(); new AutoNotch().Apply(copy, width);
            if (!copy.SequenceEqual(burst)) return false;
        }
        return true;
    }

    internal static bool Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "Spectraxia-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tape = new CaptureTape();
            var a = new CaptureBand(960000, 101300000, 101300000, ListenMode.WFM, 200000, true, Guid.NewGuid());
            var b = a with { Rate = 2400000, Center = 7100000, Station = 7100100, Mode = ListenMode.LSB, Width = 2800, Session = Guid.NewGuid() };
            var time = DateTimeOffset.UtcNow;
            var raw = new byte[19200]; var mono = new short[480]; Array.Fill(mono, (short)123);
            var first = new TapeBlock(time, a, raw, mono, 1);
            for (int i = 0; i < 3100; i++) tape.Append(first with { Utc = time.AddMilliseconds(i * 10) });
            var pinned = new ReviewClip(tape.Pin());
            if (Math.Abs(pinned.Duration - 30) > .011 || pinned.Blocks[0].Utc < time.AddMilliseconds(980)) return false;
            var task = tape.Start(root, CaptureTracks.Both, true);
            var boundary = new TapeBlock(time.AddSeconds(31), b with { Settled = false }, new byte[48000], new short[480], 1);
            boundary.Iq[0] = 171; tape.Append(boundary);
            var final = boundary with { Utc = boundary.Utc.AddMilliseconds(10), Band = b, Pcm = new short[480] };
            final.Pcm[0] = -111; tape.Append(final);
            tape.Stop();
            string folder = task.GetAwaiter().GetResult();
            var rf = new RecordingSource(Path.Combine(folder, "wideband-001.cu8"));
            var audio = new RecordingSource(Path.Combine(folder, "audio-001.wav"));
            if (Math.Abs(rf.Duration - pinned.Duration - .02) > 1e-7 || rf.Pages != 2 || audio.Pages != 2 ||
                pinned.Blocks[^1].Band != a || tape.Pin()[^1] != final) return false;
            var end = rf.Page(1);
            if (end[^1].Band != b || !end.Any(x => x.Band == boundary.Band) ||
                !end.Any(x => x.Iq[0] == 171) || audio.Page(1)[^1].Pcm[0] != -111) return false;
            var tuned = ReviewDecoder.Dial(final, a.Center, ListenMode.AM);
            if (tuned.Hz != b.Station || tuned.Mode != ListenMode.AM) return false;
            var decoder = new ReviewDecoder();
            if (decoder.Stereo(boundary, false, null, null, new()).Any(v => v != 0)) return false;
            var wav = audio.Page(0)[0];
            if (wav.Pcm[0] != 123 || wav.Pcm[1] != 123) return false;
            var noLead = tape.Start(root, CaptureTracks.Wideband, false);
            tape.Append(final); tape.Stop();
            var rfOnly = noLead.GetAwaiter().GetResult();
            if (File.Exists(Path.Combine(rfOnly, "audio-001.wav")) || new FileInfo(Path.Combine(rfOnly, "wideband-001.cu8")).Length != 48000) return false;
            var waveOnly = CaptureTape.Export(root, CaptureTracks.Audio, [final]);
            if (File.Exists(Path.Combine(waveOnly, "wideband-001.cu8"))) return false;
            // A bad destination must fault the writer without poisoning the live ring or the next take.
            var failed = tape.Start(Path.Combine(waveOnly, "audio-001.wav"), CaptureTracks.Audio, false);
            tape.Append(final); tape.Stop();
            try { failed.GetAwaiter().GetResult(); return false; } catch (IOException) { }
            return tape.Pin()[^1] == final && CheckAudioFailure();
        }
        finally { RemoveScratch(root); }
    }

    private static bool CheckAudioFailure()
    {
        int attempts = 0;
        using var audio = new LiveAudioOutput(0, (_, _) =>
        {
            attempts++;
            throw new IOException("Test audio endpoint unavailable");
        });
        short[] pcm = [123, -456];
        audio.Play(pcm, 100, true, false, 0);
        audio.Play(pcm, 100, true, false, 100);
        if (attempts != 1 || audio.Error == null) return false;
        audio.Play(pcm, 100, true, false, 1600);
        return attempts == 2 && pcm is [123, -456];
    }
    internal static int ReplayFixture(string input, int rate, long center)
    {
        if (!Radio.SampleRates.Contains(rate)) throw new InvalidDataException("Unsupported fixture rate.");
        string root = Path.Combine(Path.GetTempPath(), "Spectraxia-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tape = new CaptureTape(); var settings = new ReceiverSettings(SampleRate: rate);
            var receiver = new ReceiverAudio(rate); var meta = new CaptureBand(rate, center, center, ListenMode.WFM, settings.Width, true, Guid.NewGuid());
            var epoch = DateTimeOffset.UtcNow;
            using var file = File.OpenRead(input);
            var task = tape.Start(root, CaptureTracks.Both, false);
            long pairs = 0, frames = 0; string output;
            try
            {
                // Exercise the bounded disk queue at a modest lead over real time.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (file.Position < file.Length)
                {
                    var iq = new byte[Math.Min(32768, file.Length - file.Position)]; file.ReadExactly(iq);
                    var pcm = receiver.Decode(iq, center, center, settings, 100);
                    tape.Append(new(epoch.AddSeconds(pairs / (double)rate), meta, iq, pcm, 2));
                    pairs += iq.Length / 2; frames += pcm.Length / 2;
                    int delay = (int)(pairs * 500.0 / rate - clock.Elapsed.TotalMilliseconds);
                    if (delay > 0) Thread.Sleep(delay);
                }
            }
            finally { tape.Stop(); output = task.GetAwaiter().GetResult(); }
            string captured = Path.Combine(output, "wideband-001.cu8");
            file.Position = 0; using var copied = File.OpenRead(captured);
            if (!SHA256.HashData(file).SequenceEqual(SHA256.HashData(copied))) return 1;
            var rf = new RecordingSource(captured);
            var wav = new RecordingSource(Path.Combine(output, "audio-001.wav"));
            var player = new ReviewDecoder();
            using var decodedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var storedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long replayFrames = 0;
            var firstWindow = new ReviewClip(rf.Page(0));
            foreach (var (window, _, index) in firstWindow.From(0,0,rf,CancellationToken.None))
            {
                var pcm = player.Stereo(window.Blocks[index], false, null, null, settings);
                decodedHash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan()));
                replayFrames += pcm.Length / 2;
            }
            for (int page = 0; page < wav.Pages; page++)
            {
                foreach (var block in wav.Page(page))
                    storedHash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.Pcm.AsSpan()));
            }
            bool same = decodedHash.GetHashAndReset().SequenceEqual(storedHash.GetHashAndReset());
            Console.WriteLine($"Raw I/Q SHA-256 matched. PCM frames {frames}, offline replay {replayFrames}, identical PCM {same}. Last station name [{receiver.StationName}].");
            return frames == replayFrames && same ? 0 : 1;
        }
        finally { RemoveScratch(root); }
    }
    private static void RemoveScratch(string root)
    {
        if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
            !Path.GetFileName(root).StartsWith("Spectraxia-check-", StringComparison.Ordinal)) throw new IOException("Unexpected scratch path.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
