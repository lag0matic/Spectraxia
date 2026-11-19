namespace Spectraxia;

internal static class AirScannerCheck
{
    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }
    internal static int Run(string folder)
    {
        try
        {
            Geometry();
            Scripted(folder);
            Recorded(folder);
            WindowRestore();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Geometry()
    {
        var silence = Enumerable.Repeat(-80f, Spectrum.Bins).ToArray();
        foreach (int rate in Radio.SampleRates)
        foreach (bool narrow in new[] {false,true})
        {
            var scan = new AirScanner(AirScanner.MinHz, 137000000, narrow, rate, []);
            int windows = scan.SliceCount;
            long prior = 0, clock = 0;
            for (int n = 0; n < windows; n++)
            {
                Require(n == 0 || scan.FirstHz - prior is 8333 or 8334 or 25000, "Gap in coverage");
                Require(scan.FirstHz >= scan.Center - rate/2 + 10000 && scan.LastHz <= scan.Center + rate/2 - 10000, "Window edges");
                prior = scan.LastHz;
                scan.Observe(scan.Center, silence, clock);
                scan.Observe(scan.Center, silence, clock + 150);
                clock += 300;
            }
            Require(scan.SliceNumber == 1 && prior == (narrow ? 136991667 : 136975000), "Sweep wrap");
        }
    }

    private static void Scripted(string folder)
    {
        const long a = 118025000, b = 118125000, carrier = 118175000;
        var locks = new HashSet<long>();
        var scan = new AirScanner(118000000, 118200000, false, 960000, locks) { DelayMs = 450 };
        using var file = File.OpenRead(Path.Combine(folder, "scripted.cu8"));
        var samples = new byte[96000];
        var audio = new ReceiverAudio(960000);
        var normal = new ReceiverSettings();
        float[] last = [];
        for (int now = 0; now < 6000; now += 50)
        {
            file.ReadExactly(samples);
            last = Spectrum.Analyze(samples);
            scan.Observe(scan.Center, last, now);
            var target = scan.Target;
            var pcm = audio.Decode(samples, target.Center, target.Station, target.Listen(normal), 100);
            target.Gate(pcm, true, target.Center);
            if (now is 550 or 2650)
            {
                int tone = now == 550 ? 900 : 1400;
                Require(TonePower(pcm, tone) > 20 * Math.Max(1, TonePower(pcm, 2000)), "Wrong demodulated AM tone");
            }
            switch (now)
            {
                case 150: Require(scan.Channel == null, "Noise opened scanner"); break;
                case 350: Require(scan.Channel == a && scan.Signal, "First transmission"); break;
                case 900: Require(scan.Channel == a && !scan.Signal, "Reply gap"); break;
                case 1150: Require(scan.Channel == a && scan.Signal, "Reply"); break;
                case 2050: Require(scan.Channel == null, "Reply delay expired"); break;
                case 2400:
                    Require(scan.Channel == b, "Second channel");
                    scan.ToggleHold(); break;
                case 3750:
                    Require(scan.Held && scan.Channel == b && !scan.Signal, "Manual hold during silence");
                    scan.ToggleHold();
                    scan.Skip(now, false); break;
                case 4000: Require(scan.Channel == carrier, "Steady carrier"); scan.Skip(now, true); break;
                case 4400: Require(scan.Channel == null, "Skipped or locked frequency recaptured"); break;
                case 4600: Require(scan.Channel == a, "Resumed scan"); break;
            }
        }
        scan.Observe(scan.Center, last, 20000);
        scan.Observe(scan.Center, last, 20050);
        Require(scan.Channel == b && locks.SetEquals([carrier]), "Skip expiry or lockout lifetime");
        Console.WriteLine("Scripted I/Q passed: noise, call, reply delay, manual hold, skip expiry, lockout and resume.");
    }

    private static double TonePower(short[] pcm, int hz)
    {
        double real = 0, imag = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            var (sin, cos) = Math.SinCos(2 * Math.PI * i * hz / 48000);
            real += pcm[i] * cos;
            imag += pcm[i] * sin;
        }
        return real * real + imag * imag;
    }

    private static void Recorded(string folder)
    {
        const int rate = 1920000;
        var scan = new AirScanner(118800000, 120625000, false, rate, []);
        using var lower = File.OpenRead(Path.Combine(folder, "119537500.cu8"));
        using var upper = File.OpenRead(Path.Combine(folder, "120462500.cu8"));
        var decoder = new ReceiverAudio(rate);
        var normal = new ReceiverSettings();
        var audible = new Dictionary<long, int>();
        var hits = new List<long>();
        var block = new byte[32768];
        long center = 0, settledAt = 0, nextFrame = 0;
        for (long pos = 0; pos + block.Length <= Math.Min(lower.Length, upper.Length); pos += block.Length)
        {
            long now = pos * 500 / rate;
            var target = scan.Target;
            if (center != target.Center) { center = target.Center; settledAt = now + 75; }
            var source = center == 119537500 ? lower : upper;
            Require(center == 119537500 || center == 120462500, "Unexpected slice center");
            source.Position = pos;
            source.ReadExactly(block);
            var pcm = decoder.Decode(block, center, target.Station, target.Listen(normal), 100);
            target.Gate(pcm, now >= settledAt, center);
            if (!target.Audible || now < settledAt) Require(pcm.All(v => v == 0), "Audio leaked while searching");
            else if (pcm.Any(v => Math.Abs((int)v) > 16))
                audible[target.Station] = audible.GetValueOrDefault(target.Station) + pcm.Length;
            if (now < settledAt || now < nextFrame) continue;
            nextFrame = now + 48;
            var prior = scan.Channel;
            scan.Observe(center, Spectrum.Analyze(block, decoder.FilteredIq), now);
            if (scan.Channel is long hz && hz != prior)
            {
                hits.Add(hz);
                Console.WriteLine($"{now/1000.0:F2}s  {hz/1e6:F3} MHz");
            }
        }
        foreach (long hz in new long[] {118825000,120625000})
            Require(hits.Contains(hz) && audible.GetValueOrDefault(hz) > 24000, $"Missing recorded AM audio on {hz}");
        Console.WriteLine("Recorded ATC passed: AM audio on two channels in separate capture windows.");
    }

    private static void WindowRestore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spectraxia-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "frequency.txt");
            File.WriteAllText(path, "101.300");
            string lockPath = Path.ChangeExtension(path, ".scan-locks.json");
            File.WriteAllText(lockPath, "[118025000]");
            using var window = new ReceiverWindow(path);
            IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(child => All(child).Prepend(child));
            var plot = All(window).OfType<Spectrum>().Single();
            var panel = All(window).OfType<AirScanPanel>().Single();
            var start = panel.Controls.OfType<Button>().Single(b => b.Text == "Start");
            var original = plot.View;
            original.Zoom(240000);
            var prior = (original.Station, original.CaptureCenter, original.Span, original.Left, plot.Mode, plot.FilterHz);
            // Dispatch the actual button handlers on an unshown form; Shown starts USB acquisition.
            var click = typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            click.Invoke(start, [EventArgs.Empty]);
            Require(panel.Scan != null && plot.Mode == ListenMode.AM && plot.View.Station == 118000000, "Start from WFM");
            var clear = panel.Controls.OfType<Button>().Single(b => b.Text == "Clear locks (1)");
            click.Invoke(clear, [EventArgs.Empty]);
            Require(File.ReadAllText(lockPath) == "[]", "Clearing saved lockouts");
            click.Invoke(start, [EventArgs.Empty]);
            Require(panel.Scan == null && ReferenceEquals(original, plot.View) &&
                prior == (plot.View.Station, plot.View.CaptureCenter, plot.View.Span, plot.View.Left, plot.Mode, plot.FilterHz), "Stop/restore");
            Console.WriteLine("Main window Start/Stop restored the original VFO, mode, width, capture center and zoom.");
        }
        finally { Directory.Delete(dir, true); }
    }
}
