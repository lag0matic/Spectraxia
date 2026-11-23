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
            Favorites(folder);
            Scripted(folder);
            Recorded(folder);
            WindowRestore();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Favorites(string folder)
    {
        var a = new StationMemory(Guid.NewGuid(), "Test AM", new(118025000, ListenMode.AM, 8000));
        var b = new StationMemory(Guid.NewGuid(), "Test repeater", new(443100000, ListenMode.NFM, 12500));
        var scan = new FavoriteScanner([a,b],960000,-55) { DelayMs = 500 };
        long center = scan.Target.Center;
        scan.Observe(center+1,true,0);
        scan.Observe(center,false,50);
        scan.Observe(center,false,235);
        var target = scan.Target;
        var normal = new ReceiverSettings();
        Require(scan.Current == b && target.Listen(normal) is { Mode: ListenMode.NFM, Width: 12500, SquelchEnabled:true }, "Favorite tuning and dwell");
        scan.Observe(center,true,300);
        scan.Observe(target.Center,true,400);
        Require(!scan.Open,"Stale or single frame opened favorite");
        scan.Observe(target.Center,true,450);
        Require(scan.Open,"Favorite did not open");
        scan.Observe(target.Center,false,500);
        Require(!scan.Open && scan.Current == b,"Reply gap");
        scan.ToggleHold();
        scan.Observe(target.Center,false,2000);
        Require(scan.Held && scan.Current == b,"Hold during silence");
        scan.Skip();
        Require(scan.Current == a && !scan.Held,"Favorite skip and wrap");
        // Play the scripted recording at its actual RF center through the listening decoder's squelch.
        var air = new AirScanner(118000000,118200000,false,960000,[]);
        using var iq = File.OpenRead(Path.Combine(folder,"scripted.cu8"));
        var samples = new byte[96000];
        var decoder = new ReceiverAudio(960000);
        scan = new FavoriteScanner([a],960000,-40);
        var settings = scan.Target.Listen(normal);
        bool heard = false;
        for (int t=0;t<=700;t+=50)
        {
            iq.ReadExactly(samples);
            decoder.Decode(samples,air.Center,a.Dial.Hz,settings,100);
            scan.Observe(scan.Target.Center,decoder.Gate,t);
            if (t <= 200) Require(!scan.Open,"Recorded noise opened favorite");
            if (t >= 350 && scan.Open) heard = true;
        }
        Require(heard,"Recorded transmission did not open favorite");
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
        var normal = new ReceiverSettings(VoiceFilterEnabled:true, ReduceNoise:true);
        var voice = new VoiceFilters();
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
            var active = target.Listen(normal);
            var pcm = decoder.Decode(block, center, target.Station, active, 100);
            target.Gate(pcm, now >= settledAt, center);
            voice.Apply(pcm, active, target.Station, -1, target.Audible && now >= settledAt && center == target.Center);
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
            var voiceToggle = All(window).OfType<CheckBox>().Single(c => c.Text == "Voice filters");
            var reducer = All(window).OfType<CheckBox>().Single(c => c.Text == "Reduce steady noise");
            var high = All(window).OfType<TrackBar>().Single(c => c.AccessibleName == "High-pass");
            var low = All(window).OfType<TrackBar>().Single(c => c.AccessibleName == "Low-pass");
            Require(!voiceToggle.Checked, "Voice filters should start bypassed");
            voiceToggle.Checked = reducer.Checked = true;
            high.Value = Array.IndexOf(VoiceFilters.HighPassOptions,300);
            low.Value = Array.IndexOf(VoiceFilters.LowPassOptions,2600);
            var profile = ReceiverSettings.Load(Path.ChangeExtension(path, ".receiver.json"));
            Require(profile is { VoiceFilterEnabled:true, ReduceNoise:true, HighPassHz:300, LowPassHz:2600 },
                "Voice controls did not save their settings");
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
            var fm = new StationMemory(Guid.NewGuid(),"Repeater",new(146940000,ListenMode.NFM,12500));
            var am = new StationMemory(Guid.NewGuid(),"Tower",new(118200000,ListenMode.AM,6000));
            panel.StartFavorites([fm,am],-45);
            Require(plot.View.Station == fm.Dial.Hz && plot.Mode == ListenMode.NFM && plot.FilterHz == 12500,"Favorite scan start");
            click.Invoke(panel.Controls.OfType<Button>().Single(b => b.Text == "Skip"), [EventArgs.Empty]);
            Require(plot.View.Station == am.Dial.Hz && plot.Mode == ListenMode.AM && plot.FilterHz == 6000,"Favorite mode switch");
            panel.Stop();
            Require(ReferenceEquals(original,plot.View) && prior == (plot.View.Station, plot.View.CaptureCenter, plot.View.Span, plot.View.Left, plot.Mode, plot.FilterHz),"Favorite scan restore");
            var library = new FrequencyLibrary();
            library.Memories.AddRange([fm,am]);
            StationMemory[] scanned = [];
            DialSetting? recalled = null;
            using var favorites = new FavoritesWindow(library, () => fm.Dial, () => "", dial => recalled = dial,
                () => {}, group => { scanned=group; return true; },true);
            void Press(string text) => click.Invoke(All(favorites).OfType<Button>().Single(b => b.Text == text),[EventArgs.Empty]);
            Press("2m ham band");
            Press("Scan checked (1)");
            Require(scanned.SequenceEqual([fm]),"Scan visible favorites only");
            var include = All(favorites).OfType<CheckBox>().Single();
            include.Checked = false;
            Require(!library.Memories[0].IncludeInScan && !All(favorites).OfType<Button>().Single(b=>b.Text=="Scan checked (0)").Enabled,"Favorite exclusion");
            click.Invoke(All(favorites).OfType<Button>().Single(b=>b.Text.StartsWith("Repeater")),[EventArgs.Empty]);
            Require(recalled == fm.Dial,"Favorite button recall");
            All(favorites).OfType<TextBox>().Single(b=>b.AccessibleName=="Category").Text = "Club";
            Press("Save labels");
            Require(library.Memories[0].Group=="Club","Favorite category");
            Console.WriteLine("Airband and favorite scans restored the original dial. Favorite group filtering, exclusions and recall passed.");
        }
        finally { Directory.Delete(dir, true); }
    }
}
