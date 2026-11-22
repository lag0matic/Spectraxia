using System.Globalization;

namespace Spectraxia;

internal static class Program
{
    internal static Icon AppIcon { get; } = new(typeof(Program), "Spectraxia.ico");
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--check-airband", var fixtures])
        {
            Environment.ExitCode = AirScannerCheck.Run(fixtures);
            return;
        }
        if (args is ["--check-hd" or "--check-hd-stream", var recording, var sampleRate, var offset, var service])
        {
            Environment.ExitCode = HdCheck.Replay(recording, int.Parse(sampleRate), long.Parse(offset), int.Parse(service),
                args[0] == "--check-hd-stream");
            return;
        }
        if (args is ["--check-adsb"]) { Environment.ExitCode = AdsbCheck.Run() ? 0 : 1; return; }
        if (args.FirstOrDefault()=="--check-adsb-map")
        {
            Environment.ExitCode = args.Length<=2 && AdsbCheck.MapDetail(args.ElementAtOrDefault(1)) ? 0 : 1;
            return;
        }
        if (args is ["--replay-adsb", var iqPath]) { Environment.ExitCode = AdsbCheck.Replay(iqPath); return; }
        if (args is ["--replay-hackrf-adsb", var rawPath]) { Environment.ExitCode = AdsbCheck.Replay(rawPath, true); return; }
        if (args is ["--check-hackrf-live"]) { Environment.ExitCode = AdsbCheck.CheckLiveHackrf(); return; }
        if(args is ["--check-recording",var path,var rate,var center])
        {
            Environment.ExitCode=RecordingCheck.ReplayFixture(path,int.Parse(rate),long.Parse(center));
            return;
        }
        if (args.FirstOrDefault() is "--replay-iq" or "--capture-iq") { Environment.ExitCode = IqReplay.Run(args); return; }
        if (args.Contains("--self-test"))
        {
            if (AppPaths.Portable)
            {
                string root = AppContext.BaseDirectory;
                string nested = Path.Combine(root, "My recordings", "Airband");
                if (AppPaths.Settings != Path.Combine(root, "Data", "frequency.txt") ||
                    AppPaths.Recordings(null) != Path.Combine(root, "Recordings") ||
                    AppPaths.SaveRecordingFolder(nested) != Path.Combine("My recordings", "Airband") ||
                    AppPaths.Recordings(AppPaths.SaveRecordingFolder(nested)) != nested)
                { Environment.ExitCode = 1; return; }
            }
            if (!AdsbCheck.Run() || !CheckBiasT() || !BroadcastCheck.Run() || !SignalCheck() || !CheckModes() || !CheckChannel(-200_000) || !CheckChannel(200_000) ||
                !CheckChannel(-800_000, 1_920_000) || !CheckChannel(950_000, 2_400_000) ||
                !CheckView() || !CheckSweep() || !CheckClockAvoidance() || !CheckPixelPeaks() || !CheckDisplay() || !CheckReceiverControls() || !CheckLibrary() ||
                !CheckBandPlan() || !CheckAudioAgc() || !RfFilterCheck.Run() || !VoiceFilterCheck.Run() ||
                !RecordingCheck.AutoNotchWorks() || !RecordingCheck.Run() ||
                Tuning.Step(14_230_000, 1_000_000, 120) != 15_230_000 ||
                Tuning.Step(14_230_000, 1_000, -120) != 14_229_000 ||
                !Tuning.Parse("101.3", out var hz) || hz != 101_300_000 ||
                Tuning.Parse("3000", out _) || Tuning.Parse("NaN", out _))
                Environment.ExitCode = 1;
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new ReceiverWindow());
    }
    private static bool CheckLibrary()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spectraxia-library-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Memories":[{"Id":"00000000-0000-0000-0000-000000000001","Name":"Old FM","Dial":{"Hz":101300000,"Mode":"WFM","Width":145000}}]}""");
            var library = FrequencyLibrary.Load(path);
            if (library.Memories.Single().Dial.HdProgram != null) return false;
            library.Memories.Clear();
            var fm = new DialSetting(101300000, ListenMode.WFM, 145000);
            library.Remember(fm);
            var hf = library.Recall("40m ham band") with { Hz = 7195000, Width = 2400 };
            if (hf.Mode != ListenMode.LSB) return false;
            library.Remember(hf);
            var memory = new StationMemory(Guid.NewGuid(), "G 1013", fm with { HdProgram = 1 });
            library.Remember(memory.Dial);
            library.Memories.Add(memory);
            library.Save(path);
            var loaded = FrequencyLibrary.Load(path);
            var receiver = new ReceiverSettings(Manual: true, DigitalAgc: true);
            var hfReceiver = loaded.Recall("40m ham band").Apply(receiver);
            var fmReceiver = loaded.Recall("FM broadcast").Apply(hfReceiver);
            return loaded.Memories.Single() == memory && loaded.Recall("FM broadcast") == fm &&
                loaded.Recall("40m ham band") == hf && hfReceiver.Mode == hf.Mode && hfReceiver.Width == hf.Width &&
                fmReceiver.Mode == fm.Mode && fmReceiver.Width == fm.Width && fmReceiver.Manual && fmReceiver.DigitalAgc &&
                BandPlan.At(145900000) == BandPlan.At(146520000) && BandPlan.At(7300000) == null;
        }
        finally { File.Delete(path); }
    }

    private static bool CheckBandPlan()
    {
        long cb = 26_965_000;
        for (int channel=1; channel<=40; channel++)
        {
            if (BandPlan.CbChannel(cb,out long offset)!=channel || offset!=0) return false;
            cb=BandPlan.Step(cb,1,ListenMode.AM);
        }
        if (BandPlan.CbChannel(27_186_125,out long fine)!=19 || fine!=1125 ||
            BandPlan.CbChannel(27_255_000,out _)!=23 || BandPlan.CbChannel(27_235_000,out _)!=24 ||
            BandPlan.CbChannel(27_245_000,out _)!=25 || BandPlan.CbChannel(26_995_000,out _)!=null ||
            BandPlan.CbChannel(27_180_000,out _)!=null || BandPlan.CbChannel(27_410_000,out _)!=null) return false;
        foreach (string key in BandPlan.Choices)
        {
            var start = BandPlan.Start(key);
            if (!start.Valid || BandPlan.At(start.Hz) != key) return false;
        }
        var air = BandPlan.Start("Civil airband (voice)");
        return air.Mode == ListenMode.AM && BandPlan.At(117999999) != BandPlan.At(118000000)
            && BandPlan.At(136975000) == BandPlan.At(air.Hz) && BandPlan.At(137000000) == "VHF satellite downlinks"
            && BandPlan.At(328599999) == BandPlan.At(335400000) && BandPlan.At(328600000) == "ILS glide slope"
            && BandPlan.At(1694100000) == "L-band weather satellites"
            && BandPlan.Step(122801000, 1, ListenMode.AM) == 122825000
            && BandPlan.Step(122801000, -1, ListenMode.AM) == 122800000;
    }

    private static bool CheckAudioAgc()
    {
        short[] original = [short.MinValue, 4000];
        var untouched = original.ToArray();
        new AudioAgc().Apply(untouched, 2, AgcSpeed.Off);
        if (!untouched.SequenceEqual(original)) return false;
        int last = int.MaxValue;
        foreach (var speed in new[] { AgcSpeed.Fast, AgcSpeed.Medium, AgcSpeed.Slow })
        {
            var agc = new AudioAgc();
            short[] loud = [short.MinValue, 16384];
            agc.Apply(loud, 2, speed);
            if (loud[0] != -8192 || loud[1] != 4096) return false;
            var quiet = Enumerable.Repeat((short)2000, 12000).ToArray();
            agc.Apply(quiet, 1, speed);
            if (quiet[^1] <= quiet[0] || quiet[^1] >= last) return false;
            last = quiet[^1];
            short[] jump = [30000];
            agc.Apply(jump, 1, speed);
            if (jump[0] is < 8191 or > 8192) return false;
        }
        string path = Path.Combine(Path.GetTempPath(), $"spectraxia-audio-agc-{Guid.NewGuid():N}.json");
        try
        {
            var am = new ReceiverSettings(Mode: ListenMode.AM).WithAudioSpeed(AgcSpeed.Fast);
            (am with { Mode = ListenMode.LSB }).WithAudioSpeed(AgcSpeed.Slow).Save(path);
            var loaded = ReceiverSettings.Load(path);
            return loaded.AudioSpeed == AgcSpeed.Slow && (loaded with { Mode = ListenMode.AM }).AudioSpeed == AgcSpeed.Fast
                && (loaded with { Mode = ListenMode.WFM }).AudioSpeed == AgcSpeed.Off
                && (loaded with { Mode = ListenMode.IQ }).AudioSpeed == AgcSpeed.Off;
        }
        finally { File.Delete(path); }
    }

    private static bool CheckBiasT()
    {
        var session = new BiasT();
        var commands = new List<bool>();
        int Set(bool on) { commands.Add(on); return 0; }
        session.Toggle();
        session.Open(Set);
        session.Apply(Set);
        session.Toggle();
        session.Toggle();
        session.Apply(Set);
        if (session.State != BiasState.On) return false;
        session.Toggle();
        session.Apply(Set);
        if (session.State != BiasState.Off) return false;
        session.Toggle();
        session.Apply(Set);
        session.Close(Set);
        session.Toggle();
        session.Apply(Set);
        if (session.State != BiasState.Unknown || !commands.SequenceEqual(new[] { false,true,false,true,false })) return false;
        var reconnect = new BiasT();
        reconnect.Open(Set);
        reconnect.Toggle();
        try { reconnect.Apply(_ => -1); return false; }
        catch (IOException) { return reconnect.State == BiasState.Unknown; }
    }

    private static bool CheckReceiverControls()
    {
        var channel = new FmChannel();
        var iq = new byte[32768];
        for (int i = 0; i < iq.Length; i += 2) { iq[i] = 191; iq[i + 1] = 128; }
        channel.Extract(iq, 0);
        channel.Extract(iq, 0);
        if (Math.Abs(channel.LevelDbfs + 6.05) > .05) return false;
        for (int i = 0; i < iq.Length; i += 2)
        {
            double phase = Math.PI * i * 75_000 / Radio.CaptureRate;
            iq[i] = (byte)(127.5 + 80 * Math.Cos(phase));
            iq[i+1] = (byte)(127.5 + 80 * Math.Sin(phase));
        }
        channel.Extract(iq, 0, 200);
        double wide = channel.LevelDbfs;
        channel.Extract(iq, 0, 50);
        channel.Extract(iq, 0, 50);
        if (wide - channel.LevelDbfs < 35) return false;
        var config = new ReceiverSettings(true, 213, true, true, -40, AverageTrace: true, PeakHold: true,
            HoldSeconds: 12, WaterfallMin: -64, WaterfallMax: -12);
        string path = Path.Combine(Path.GetTempPath(), $"spectraxia-{Guid.NewGuid():N}.json");
        try
        {
            if (ReceiverSettings.Load(path) != new ReceiverSettings()) return false;
            config.Save(path);
            if (ReceiverSettings.Load(path) != config) return false;
            File.WriteAllText(path, """{"WfmSquelch":true,"WfmThreshold":-47}""");
            var legacy = ReceiverSettings.Load(path);
            if (!legacy.SquelchEnabled || legacy.SquelchDbfs != -47 || legacy.HoldSeconds != 3) return false;
            File.WriteAllText(path, "{");
            if (ReceiverSettings.Load(path) != new ReceiverSettings()) return false;
        }
        finally { File.Delete(path); }
        return config.NearestGain([0, 207, 254]) == 207 && config.PassSignal(-40, false) &&
            !config.PassSignal(-41, false) && config.PassSignal(-41, true) && !config.PassSignal(-43, true) &&
            new ReceiverSettings().PassSignal(-95, false);
    }

    private static bool CheckDisplay()
    {
        using var plot = new Spectrum { Size = new Size(400, 300) };
        using var frame = new Bitmap(400, 300);
        var row = Enumerable.Repeat(-50f, Spectrum.Bins).ToArray();
        for (int i = 0; i < 48; i++) plot.Add(plot.View.CaptureCenter, row);
        plot.DrawToBitmap(frame, plot.ClientRectangle);
        int historyColor = frame.GetPixel(50, 158).ToArgb();
        int traceColor = frame.GetPixel(50, 66).ToArgb();
        plot.SetWaterfallLimits(-54, -50);
        plot.DrawToBitmap(frame, plot.ClientRectangle);
        if (frame.GetPixel(50, 158).ToArgb() == historyColor || frame.GetPixel(50, 66).ToArgb() != traceColor) return false;
        plot.ChangeCapture(Radio.CaptureRate);
        long oldCenter = plot.View.CaptureCenter;
        var stripe = Enumerable.Repeat(-95f, Spectrum.Bins).ToArray();
        Array.Fill(stripe, -10f, Spectrum.Bins * 58 / 100, Spectrum.Bins / 16);
        for (int i = 0; i < 70; i++) plot.Add(oldCenter, stripe);
        plot.DrawToBitmap(frame, plot.ClientRectangle);
        int strong = frame.GetPixel(240, 165).ToArgb();
        if (strong == frame.GetPixel(140, 165).ToArgb()) return false;
        plot.View.Tune(oldCenter + Radio.CaptureRate / 4, true);
        plot.DrawToBitmap(frame, plot.ClientRectangle);
        if (strong != frame.GetPixel(140, 165).ToArgb()) return false;
        plot.Add(plot.View.CaptureCenter, row);
        plot.DrawToBitmap(frame, plot.ClientRectangle);
        if (strong != frame.GetPixel(140, 165).ToArgb() || strong == frame.GetPixel(240, 165).ToArgb()) return false;
        return Spectrum.NextPeak(-20, -70, 0, 2000, 2900, 3) == -20
            && Spectrum.NextPeak(-20, -70, 0, 2900, 3500, 3) == -23
            && Spectrum.NextPeak(-20, -10, 0, 3500, 4000, 3) == -10;
    }

    private static bool CheckSweep()
    {
        foreach (int span in new[] { 2400000, 300000 })
        foreach (int direction in new[] { 1, -1 })
        {
            const long start = 7_100_000;
            var view = new TuningView(start) { Mode = ListenMode.LSB, FilterHz = 2800 };
            view.ChangeCapture(2400000);
            view.Zoom(span);
            int changes = 0;
            for (int i = 1; i <= 2000; i++)
            {
                long before = view.CaptureCenter;
                view.Tune(start + i*1000*direction, false);
                if (view.CaptureCenter != before) changes++;
                if (!TuningView.Fits(view.Station, view.CaptureCenter, view.SampleRate, view.Mode, view.FilterHz)) return false;
            }
            if (changes is 0 or > 12) return false;
        }
        return true;
    }
    private static bool CheckClockAvoidance()
    {
        const long clock = 28_800_000;
        foreach (int rate in Radio.SampleRates)
        foreach (var mode in Enum.GetValues<ListenMode>())
        {
            var view = new TuningView(27_900_000) { Mode = mode, FilterHz = Listening.Range(mode).Max };
            view.ChangeCapture(rate);
            var edges = Listening.Edges(mode, view.FilterHz);
            // Touch, straddle, or just miss the clock with either edge, in both tuning directions.
            long below = clock - (long)edges.High, above = clock - (long)edges.Low;
            long[] stations = [27_185_000, 27_928_000, below-2, below-1, below, clock, above, above+1,
                above+2, 28_970_000, 29_600_000, 101_300_000];
            foreach (bool zoomed in new[]{false,true})
            {
                view.Zoom(zoomed ? rate/8 : rate);
                foreach (long hz in stations.Concat(stations.Reverse()))
                {
                    view.Tune(hz, false);
                    long lo=view.CaptureCenter-rate/2, hi=lo+rate;
                    bool inside = lo<=clock && clock<=hi;
                    if (inside != (hz>=below && hz<=above) || view.Station!=hz ||
                        !TuningView.Fits(hz,view.CaptureCenter,rate,mode,view.FilterHz) ||
                        view.Left<lo || view.Left+view.Span>hi || hz<view.Left || hz>view.Left+view.Span) return false;
                }
            }
            view.ChangeCapture(rate);
            view.Tune(27_900_000,true);
            if (rate==2_400_000 && view.CaptureCenter!=27_400_000) return false;
            long center=view.CaptureCenter;
            for (long hz=27_900_000; hz<=27_920_000; hz+=1000)
            {
                view.Tune(hz,false,rate==2_400_000 ? view.Station-view.Left : null);
                if (view.CaptureCenter!=center || view.Station!=hz) return false;
            }
        }
        return true;
    }

    private static bool CheckPixelPeaks()
    {
        foreach (int width in new[] { 320, 617 })
        foreach (int span in new[] { 960000, 713591, 480000 })
        {
            using var plot = new Spectrum { Size = new Size(width, 180) };
            plot.ChangeCapture(960000);
            plot.View.Zoom(span);
            var row = Enumerable.Repeat(-95f, Spectrum.Bins).ToArray();
            // Put one carrier at each phase of the bin-to-pixel grouping.
            for (int i = 0; i < 12; i++) row[700 + i*13] = 0;
            for (int i = 0; i < 64; i++) plot.Add(plot.View.CaptureCenter, row);
            using var image = new Bitmap(width,180);
            plot.DrawToBitmap(image, plot.ClientRectangle);
            for (int i = 0; i < 12; i++)
            {
                long hz = plot.View.CaptureCenter - 480000 + (700 + i*13)*960000L/Spectrum.Bins;
                int x = (int)Math.Round((hz-plot.View.Left)*(width-1.0)/span);
                if (!Enumerable.Range(x-1,3).Any(at => image.GetPixel(at,96).R > 200)) return false;
            }
        }
        return true;
    }

    private static bool CheckView()
    {
        foreach (int rate in Radio.SampleRates)
        {
            var sweep = new TuningView(101_300_000);
            sweep.ChangeCapture(rate);
            sweep.Tune(sweep.FrequencyAt(.625),false);
            long initialCenter = sweep.CaptureCenter, tuned = sweep.Station;
            for (int tick = 1; tick <= TuningView.ZoomMaximum; tick++)
            {
                int span = TuningView.ZoomSpan(rate,tick);
                if (span >= sweep.Span || span < sweep.Span*.996) return false;
                sweep.Zoom(span);
                if (sweep.CaptureCenter != initialCenter || sweep.Station != tuned ||
                    Math.Abs(sweep.FrequencyAt(.625)-tuned) > span/2000.0) return false;
            }
            if (sweep.Span != rate/32) return false;
            sweep.Zoom(TuningView.ZoomSpan(rate,0));
            if (sweep.Span != rate || sweep.Left != initialCenter-rate/2) return false;
        }
        var view = new TuningView(101_300_000);
        long center = view.CaptureCenter, left = view.Left;
        view.Tune(view.FrequencyAt(.75), false);
        if (view.Left != left || view.CaptureCenter != center) return false;
        view.Zoom(240_000);
        if (view.FrequencyAt(.75) != view.Station) return false;
        long anchor = view.Station - view.Left;
        long beforeDrag = view.Station;
        view.Tune(beforeDrag + 200_000, false, anchor);
        if (view.Station != beforeDrag + 200_000 || view.FrequencyAt(.75) != view.Station) return false;
        view.Tune(beforeDrag + 900_000, false, anchor);
        if (view.CaptureCenter == center || view.FrequencyAt(.75) != view.Station) return false;
        view.Tune(beforeDrag, false, anchor);
        center = view.CaptureCenter;
        long station = view.Station;
        view.Pan(long.MaxValue);
        if (view.Left + view.Span != center + Radio.CaptureRate / 2 || view.Station != station) return false;
        view.Tune(station + 2_000_000, false);
        if (view.Station != view.Left + view.Span || view.CaptureCenter == center) return false;
        view.Tune(station, true);
        if (view.FrequencyAt(.5) != station) return false;
        foreach (int rate in Radio.SampleRates)
        {
            view.ChangeCapture(rate);
            if (view.Span != rate || view.FrequencyAt(.5) != station) return false;
            foreach (long edge in new[] { 0L, TuningView.MaxHz, station })
            {
                view.Tune(edge, false);
                if (view.Left != view.CaptureCenter - rate / 2 || view.Station != edge) return false;
            }
            view.Tune(station, true);
        }
        return true;
    }

    private static bool CheckChannel(int offset, int rate = Radio.CaptureRate)
    {
        var channel = new FmChannel(rate);
        var dsp = new Wfm();
        var audio = new List<short>();
        var iq = new byte[8194]; // Vary decimator phase across block boundaries.
        double carrier = 0, adjacent = 0;
        int index = 0;
        for (int block = 0; block < 48; block++)
        {
            for (int i = 0; i < iq.Length; i += 2, index++)
            {
                double t = index / (double)rate;
                carrier += 2 * Math.PI / rate * (offset + 28_000 * Math.Sin(2 * Math.PI * 1000 * t));
                adjacent += 2 * Math.PI / rate * (-offset + 28_000 * Math.Sin(2 * Math.PI * 3000 * t));
                iq[i] = (byte)(127.5 + 45 * Math.Cos(carrier) + 65 * Math.Cos(adjacent));
                iq[i+1] = (byte)(127.5 + 45 * Math.Sin(carrier) + 65 * Math.Sin(adjacent));
            }
            audio.AddRange(dsp.Decode(channel.Extract(iq, offset)));
        }
        return audio.Count == index / (rate / 48000) && Power(audio, 1000) > 100 * Power(audio, 3000);
    }

    private static double Power(List<short> audio, int hz)
    {
        double i = 0, q = 0;
        for (int n = 1000; n < audio.Count; n++)
        {
            double a = n * 2 * Math.PI * hz / 48000;
            i += audio[n] * Math.Cos(a);
            q += audio[n] * Math.Sin(a);
        }
        return i*i + q*q;
    }

    private static bool SignalCheck()
    {
        // A full-scale reference is unnecessary: exercise the same chunked decoder as live USB.
        var dsp = new Wfm();
        var audio = new List<short>();
        double phase = 0;
        for (int block = 0; block < 24; block++)
        {
            var iq = new byte[8192];
            for (int n = 0; n < iq.Length / 2; n++)
            {
                double t = (block * 4096 + n) / (double)Radio.Rate;
                phase += 2 * Math.PI * 30000 / Radio.Rate * Math.Sin(2 * Math.PI * 1000 * t);
                iq[n*2] = (byte)(127.5 + 110 * Math.Cos(phase));
                iq[n*2+1] = (byte)(127.5 + 110 * Math.Sin(phase));
            }
            audio.AddRange(dsp.Decode(iq));
        }
        var carrier = new byte[2*Spectrum.Bins];
        for (int n = 0; n < Spectrum.Bins; n++)
        {
            double angle = 2 * Math.PI * 32 * n / Spectrum.Bins;
            carrier[2*n] = (byte)(127.5 + 100*Math.Cos(angle));
            carrier[2*n+1] = (byte)(127.5 + 100*Math.Sin(angle));
        }
        var bins = Spectrum.Analyze(carrier);
        return audio.Count == 24*4096/5 && Power(audio, 1000) > 100 * Power(audio, 2000)
            && Array.IndexOf(bins, bins.Max()) == Spectrum.Bins/2 + 32;
    }

    private static bool CheckModes()
    {
        foreach (var mode in Enum.GetValues<ListenMode>().Where(m => m != ListenMode.WFM))
        {
            var decoder = new NarrowReceiver(mode, Listening.Range(mode).Default);
            var pcm = new List<short>();
            double fmPhase = 0;
            for (int block = 0; block < 24; block++)
            {
                var iq = new byte[4094];
                for (int n = 0; n < iq.Length / 2; n++)
                {
                    double t = 2*Math.PI*(block*2047+n)/Radio.Rate;
                    double i = 0, q = 0, a = Math.Cos(1000*t);
                    switch (mode)
                    {
                        case ListenMode.AM: q = 70 + 35*a; break;
                        case ListenMode.DSB: i = 75*a; break;
                        case ListenMode.CW: i = 75; break;
                        case ListenMode.NFM:
                            fmPhase += 2*Math.PI*2000*a/Radio.Rate;
                            i = 80*Math.Cos(fmPhase); q = 80*Math.Sin(fmPhase); break;
                        case ListenMode.IQ: i = 60*a; q = 45*Math.Cos(2000*t); break;
                        default:
                            i = 50*a + 50*Math.Cos(2000*t);
                            q = 50*Math.Sin(1000*t) - 50*Math.Sin(2000*t); break;
                    }
                    iq[2*n]=(byte)Math.Round(127.5+i); iq[2*n+1]=(byte)Math.Round(127.5+q);
                }
                pcm.AddRange(decoder.Decode(iq));
            }
            if (mode == ListenMode.IQ)
            {
                var right = pcm.Where((_, i) => i%2 == 1).ToList();
                pcm = pcm.Where((_, i) => i%2 == 0).ToList();
                if (Power(right,2000) < 100*Power(right,1000)) return false;
            }
            int wanted = mode == ListenMode.CW ? 700 : mode == ListenMode.LSB ? 2000 : 1000;
            int reject = wanted == 2000 ? 1000 : 2000;
            if (pcm.Count != 24*2047/5 || Power(pcm,wanted) < 1e10 ||
                Power(pcm,wanted) < 100*Power(pcm,reject)) return false;
        }
        return BandPlan.Step(27_255_000, 1, ListenMode.AM) == 27_235_000;
    }

}

internal static class Tuning
{
    public static long Step(long hz, long place, int wheelDelta) =>
        Math.Clamp(hz + (wheelDelta / 120L) * place, 0, 1_766_000_000);

    public static bool Parse(string text, out long hz)
    {
        hz = 0;
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var mhz) || mhz < 0 || mhz > 1766)
            return false;
        hz = (long)decimal.Round(mhz * 1_000_000);
        return true;
    }
}
