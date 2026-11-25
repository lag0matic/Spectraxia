namespace Spectraxia;

internal static class AdsbCheck
{
    // Published independent vectors from Junzi Sun, The 1090 Megahertz Riddle.
    private const string Identification = "8D4840D6202CC371C32CE0576098";
    private const string Velocity = "8D485020994409940838175B284F";
    private const string Even = "8D40621D58C382D690C8AC2863A7";
    private const string Odd = "8D40621D58C386435CC412692AD6";

    internal static bool Run()
    {
        var traffic = new AdsbTraffic();
        long now = 0;
        var decoder = new AdsbDecoder(f => traffic.Update(f,now));
        var random = new Random(2084);
        void Send(string hex)
        {
            var data = Convert.FromHexString(hex);
            var iq = new byte[2*(27+240)];
            for (int i = 0; i < iq.Length; i++) iq[i] = (byte)(128+random.Next(-5,6));
            void Pulse(int sample) => iq[2*(sample+27)+1] += 55;
            foreach (int p in new[]{0,2,7,9}) Pulse(p);
            for (int b = 0; b < 112; b++)
                Pulse(16+2*b+(((data[b/8]>>(7-b%8)) & 1)==0 ? 1 : 0));
            for (int i=0; i<iq.Length;)
            {
                int length = Math.Min(iq.Length-i,random.Next(1,53));
                decoder.Feed(iq.AsSpan(i,length)); i+=length;
            }
        }
        Send(Identification); Send(Velocity); Send(Odd); Send(Identification[..^2]+"99");
        if (traffic.RecentMessages(now) != 3 || traffic.RecentMessages(9999) != 3 || !CheckSbs()) return false;
        now=10001; Send(Even);
        if (traffic.RecentMessages(now) != 1 || !CheckReceiverSettings() || !CheckHackrfTiming()) return false;
        var rows=traffic.Snapshot(now);
        var velocity=rows.Single(r=>r.Address=="485020");
        if (traffic.Messages!=4 || rows.Length!=3 || rows.Single(r=>r.Address=="4840D6").Callsign!="KLM1023" ||
            velocity.Speed!=159 || velocity.Track is not (>182 and <184) || velocity.Climb != -832 ||
            rows.Any(r=>r.Position.HasValue)) return false;
        now++; Send(Odd);
        var row=traffic.Snapshot(now).Single(r=>r.Address=="40621D");
        if (row.Position is not {} odd || Math.Abs(odd.Lat-52.26578)>.00003 || Math.Abs(odd.Lon-3.93891)>.00003) return false;
        now++; Send(Even);
        rows=traffic.Snapshot(now); row=rows.Single(r=>r.Address=="40621D");
        if (row.FirstSeen!=0 || row.PositionSeen!=now || row.Altitude!=38000 || row.Position is not {} pos || Math.Abs(pos.Lat-52.257202)>.00003 ||
            Math.Abs(pos.Lon-3.91937)>.00003) return false;
        using var map=new AircraftMap { Size=new Size(640,320) };
        using var image=new Bitmap(map.Width,map.Height);
        map.SetAircraft(rows,now); map.DrawToBitmap(image,map.ClientRectangle);
        var tint = AircraftSymbol.Tint(row.Category);
        if (image.GetPixel(320,160).ToArgb()!=Color.FromArgb(tint.R*2/3,tint.G*2/3,tint.B*2/3).ToArgb()) return false;
        if (!CheckMapClick(rows,row)) return false;
        if (traffic.Snapshot(now+60001).Any(r=>r.Position!=null || r.PositionSeen!=null) ||
            traffic.Snapshot(now+120001).Length!=0) return false;
        now+=120002; Send(Odd);
        return traffic.Snapshot(now).Single().FirstSeen==now && MapDetail() && CheckTrails() &&
            AircraftReference.Lookup("40621D") is { Registration:"G-STBC", TypeCode:"B77W", Model:"BOEING 777-300ER" } &&
            AircraftReference.Lookup("FFFFFF")==null;
    }

    private static bool CheckSbs()
    {
        if (BaseStationEndpoint.Parse("radopi.local") != new BaseStationEndpoint("radopi.local", 30003) ||
            BaseStationEndpoint.Parse("[::1]:30004") != new BaseStationEndpoint("::1", 30004) ||
            BaseStationEndpoint.Parse("localhost:70000") != null || BaseStationEndpoint.Parse("localhost/path") != null)
            return false;
        var traffic = new AdsbTraffic();
        string[] lines = [
            "MSG,1,1,1,ABC123,1,,,,,TEST123 ,,,,,,,,,,,0",
            "MSG,3,1,1,ABC123,1,,,,,,37000,,,39.994145,-84.472192,,,0,,0,0",
            "MSG,4,1,1,ABC123,1,,,,,,,379,266,,,-2432,,,,,0",
            "MSG,3,1,1,ABC123,1,,,,,,,,,NaN,500,,,0,,0,0"
        ];
        long now = 10;
        foreach (string line in lines)
        {
            if (SbsReport.Parse(line) is not {} report) return false;
            traffic.Update(report, now++);
        }
        var row = traffic.Snapshot(now).Single();
        if (!CheckNetworkCategory()) return false;
        traffic.Update(new SbsReport(0xABC123, "", null, null, null, 640, null), now + 65_000);
        if (traffic.Snapshot(now + 65_000).Single() is not { Speed: null, Track: null, Climb: 640 })
            return false;
        return row is { Address: "ABC123", Callsign: "TEST123", Altitude: 37000, Speed: 379, Track: 266,
            Climb: -2432, Position: { Lat: 39.994145, Lon: -84.472192 }, PositionSeen: 11, Messages: 4 } &&
            traffic.Snapshot(now+60_000).Single().Position == null &&
            SbsReport.Parse("MSG,3") == null && SbsReport.Parse(lines[0].Replace("ABC123", "ZZZZZZ")) == null;
    }

    internal static async Task<int> CheckFeed(string address)
    {
        if (BaseStationEndpoint.Parse(address) is not {} endpoint) return 1;
        var traffic = new AdsbTraffic();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await BaseStationFeed.Run(endpoint, traffic, stop.Token);
        var rows = traffic.Snapshot(Environment.TickCount64);
        int positions = rows.Count(row => row.Position.HasValue);
        var typed = rows.Where(row => row.Category != EmitterCategory.Unknown).ToArray();
        var counts = typed.GroupBy(row => row.Category).Select(g => $"{g.Count()} {AircraftSymbol.Label(g.Key)}");
        Console.WriteLine($"{traffic.Messages} SBS reports, {rows.Length} aircraft, {positions} positions, " +
            $"{typed.Length} categorized ({string.Join(", ", counts)})");
        return positions > 0 ? 0 : 1;
    }

    private static bool CheckNetworkCategory()
    {
        const string frame = "8D4840D6232CC371C32CE0CC1B88";
        var report = new SbsReport(0x4840D6, "TEST123", 31000, 401, 72, 0, new(40, -84));
        foreach (bool rawFirst in new[] { false, true })
        {
            var traffic = new AdsbTraffic();
            const long now = 500_000;
            if (rawFirst)
            {
                BaseStationFeed.ReadCategory("*" + frame + ";", traffic, now - 10);
                if (traffic.Snapshot(now).Length != 0) return false;
            }
            traffic.Update(report, now);
            if (!rawFirst)
            {
                foreach (string invalid in new[] { "*" + frame[..^1] + "9;", "*" + Identification + ";",
                    "*" + Velocity + ";", "*this is not a frame;", "*" + frame[..^1] + "Z;" })
                    BaseStationFeed.ReadCategory(invalid, traffic, now);
                if (traffic.Snapshot(now).Single().Category != EmitterCategory.Unknown) return false;
                BaseStationFeed.ReadCategory("@000000000001" + frame + ";", traffic, now + 10);
            }
            if (traffic.Snapshot(now + 11).Single() is not { Category: EmitterCategory.Large,
                Messages: 1, Seen: now, Altitude: 31000, Track: 72 } || traffic.Messages != 1 ||
                traffic.RecentMessages(now + 11) != 1 || traffic.Snapshot(now + 120001).Length != 0) return false;
        }
        return true;
    }

    private static bool CheckMapClick(AircraftRow[] rows, AircraftRow target)
    {
        using var window = new AdsbWindow(new AdsbTraffic(),new HackrfTuner(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json")));
        IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => Children(c).Prepend(c));
        var map = Children(window).OfType<AircraftMap>().Single();
        var list = Children(window).OfType<ListView>().Single();
        _ = list.Handle;
        foreach (var row in rows) list.Items.Add(new ListViewItem(row.Address) { Name = row.Address, Tag = row });
        map.CenterOn(target.Position!.Value);
        map.SetAircraft(rows,target.Seen);
        _ = map.Handle;
        var point = new Point(map.Width/2,map.Height/2);
        void Mouse(string method, Point p) => typeof(AircraftMap).GetMethod(method,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(map,[new MouseEventArgs(MouseButtons.Left,1,p.X,p.Y,0)]);
        Mouse("OnMouseDown",point);
        Mouse("OnMouseUp",point);
        if (list.SelectedItems.Count != 1 || list.SelectedItems[0].Name != target.Address) return false;
        list.SelectedItems.Clear();
        Mouse("OnMouseDown",point);
        var other = new Point(point.X+40,point.Y);
        Mouse("OnMouseMove",other);
        Mouse("OnMouseUp",other);
        return list.SelectedItems.Count == 0;
    }

    private static bool CheckReceiverSettings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spectraxia-adsb-gain-" + Guid.NewGuid().ToString("N"));
        try
        {
            var listening = new ReceiverSettings(Manual: true, GainTenths: 218, DigitalAgc: true, SampleRate: 960000);
            string radioFile = Path.Combine(dir, "frequency.receiver.json");
            listening.Save(radioFile);
            string original = File.ReadAllText(radioFile);
            string profile = Path.ChangeExtension(Path.Combine(dir, "frequency.txt"), ".adsb-gain.json");
            var tuner = new AdsbTuner(profile, listening);
            using var panel = new AdsbGainPanel(tuner);
            var slider = panel.Controls.OfType<TrackBar>().Single();
            var manual = panel.Controls.OfType<CheckBox>().Single(c => c.Text == "Manual tuner gain");
            var agc = panel.Controls.OfType<CheckBox>().Single(c => c.Text == "RTL digital AGC");
            panel.RefreshReceiver([0, 207, 280, 402], 14);
            if (!manual.Checked || !agc.Checked || !slider.Enabled || slider.Value != 1 ||
                tuner.Capture.SampleRate != AdsbDecoder.Rate || tuner.Capture.GainTenths != 218) return false;
            slider.Value = 3;
            manual.Checked = false;
            agc.Checked = false;
            if (slider.Enabled || tuner.Capture.Manual || tuner.Capture.DigitalAgc || tuner.Capture.GainTenths != 402) return false;
            panel.RefreshReceiver(null, 0);
            if (manual.Enabled || agc.Enabled) return false;
            panel.RefreshReceiver([0, 207, 280, 402], 0);
            manual.Checked = true;
            tuner.Flush();
            var restored = new AdsbTuner(profile, listening with {GainTenths = 0});
            return slider.Enabled && slider.Value == 3 && restored.Capture == tuner.Capture &&
                File.ReadAllText(radioFile) == original && tuner.FileStatus.Length == 0;
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static bool CheckTrails()
    {
        using var map = new AircraftMap { Size = new Size(600,300) };
        using var image = new Bitmap(map.Width,map.Height);
        map.CenterOn(new(0,0));
        AircraftRow Fix(string address, double lat, double lon, long time) =>
            new(address, address, null, null, null, null, new GeoPosition(lat,lon), time, 1)
                { FirstSeen=0, PositionSeen=time };
        void Update(double lon, long time) => map.SetAircraft([Fix("AAAAAA",.5,lon,time),Fix("BBBBBB",0,lon,time)],time);
        (int A,int B) Pixels()
        {
            map.DrawToBitmap(image,map.ClientRectangle);
            return (image.GetPixel(300,100).ToArgb(),image.GetPixel(300,150).ToArgb());
        }
        Update(-1,0);
        Update(1,1000);
        if (map.HitAircraft(new(400,100)) != "AAAAAA" || map.HitAircraft(new(400,150)) != "BBBBBB" ||
            map.HitAircraft(new(300,150)) != null) return false;
        var site = new GeoPosition(39,179.9);
        var east = MapRange.Destination(site,125,90);
        if (east.Lon >= 0 || Math.Abs(MapRange.Distance(site,east)-125) > .001 ||
            Math.Abs(MapRange.Distance(new(0,0),new(1,0))-60.04) > .01) return false;
        string path = Path.Combine(Path.GetTempPath(), $"spectraxia-range-{Guid.NewGuid():N}.json");
        try
        {
            var range = new MapRange(site,false);
            range.Save(path);
            if (MapRange.Load(path) != range) return false;
        }
        finally { File.Delete(path); }
        var blank = Pixels();
        map.SelectAircraft(["AAAAAA"]);
        var first = Pixels();
        if (first.A == blank.A || first.B != blank.B) return false;
        map.SelectAircraft(["BBBBBB"]);
        var second = Pixels();
        if (second.A != blank.A || second.B == blank.B) return false;
        map.SelectAircraft(["AAAAAA","BBBBBB"]);
        if (Pixels() != (first.A,second.B)) return false;
        map.SelectAircraft([]);
        if (Pixels() != blank) return false;
        map.SelectAircraft(["AAAAAA"]);
        // Continuous fresh reports at the end of the trail age out its starting point.
        for (long time=51_000;time<=651_000;time+=50_000) Update(1,time);
        if (Pixels() != blank) return false;
        // No invented path back through the midpoint after lost coverage.
        Update(-1,720_000);
        if (Pixels() != blank) return false;
        map.SetAircraft([],720_001);
        Update(1,721_000);
        return Pixels() == blank;
    }

    internal static bool MapDetail(string? previewPath = null)
    {
        var traffic = new AdsbTraffic();
        // Update receives validated payloads. Exercise attribution to the correct ICAO, retention
        // across position frames and the special meaning of category zero in an identity frame.
        var identity = Convert.FromHexString(Identification);
        var even = Convert.FromHexString(Even);
        Array.Copy(even,1,identity,1,3);
        foreach (var (value,category) in new[] {(0x25,EmitterCategory.Heavy),(0x21,EmitterCategory.Light),
            (0x27,EmitterCategory.Rotorcraft),(0x19,EmitterCategory.Glider),(0x20,EmitterCategory.Unknown)})
        {
            identity[4]=(byte)value;
            traffic.Update(identity,0); traffic.Update(even,1);
            if (traffic.Snapshot(1).Single().Category != category) return false;
        }
        static HashSet<Point> Icon(EmitterCategory category, double track)
        {
            using var bitmap=new Bitmap(48,48);
            using var g=Graphics.FromImage(bitmap);
            AircraftSymbol.Draw(g,new(24,24),category,track,1);
            var pixels=new HashSet<Point>();
            for (int y=0;y<48;y++)
            for (int x=0;x<48;x++)
                if (bitmap.GetPixel(x,y).A>127) pixels.Add(new(x,y));
            return pixels;
        }
        var heavy=Icon(EmitterCategory.Heavy,0);
        var categories = Enum.GetValues<EmitterCategory>();
        if (categories.Select(c => AircraftSymbol.Tint(c).ToArgb()).Distinct().Count() != categories.Length) return false;
        if (heavy.Count<=Icon(EmitterCategory.Light,0).Count ||
            heavy.SetEquals(Icon(EmitterCategory.Rotorcraft,0)) || heavy.SetEquals(Icon(EmitterCategory.Heavy,90))) return false;
        using var map = new AircraftMap { Size = new(1000,500), Font = new("Segoe UI",9) };
        var middle = new Point(map.Width/2,map.Height/2);
        var sample = traffic.Snapshot(1).Single();
        map.CenterOn(new(39.82,-86.18)); map.ZoomAt(middle,4);
        map.SetAircraft([
            sample with { Callsign="DEMO 1", Category=EmitterCategory.Light, Track=16, Position=new(39.94,-86.43) },
            sample with { Callsign="DEMO 2", Category=EmitterCategory.Heavy, Track=222, Position=new(39.79,-86.22) },
            sample with { Callsign="DEMO 3", Category=EmitterCategory.Rotorcraft, Track=84, Position=new(39.85,-85.98) }
        ],1);
        using var bitmap = new Bitmap(map.Width,map.Height);
        map.DrawToBitmap(bitmap,map.ClientRectangle);
        if (previewPath!=null) bitmap.Save(previewPath,System.Drawing.Imaging.ImageFormat.Png);
        // Indianapolis International has three published runways. At airport zoom the center
        // of one known runway must be painted in the runway layer, independently of the marker.
        map.CenterOn(new(39.71735,-86.30685)); map.ZoomAt(middle,14);
        map.DrawToBitmap(bitmap,map.ClientRectangle);
        for (int y=middle.Y-2; y<=middle.Y+2; y++)
        for (int x=middle.X-2; x<=middle.X+2; x++)
        {
            Color pixel=bitmap.GetPixel(x,y);
            if (pixel.R>200 && pixel.G>150 && pixel.B<160) return true;
        }
        return false;
    }

    private static bool CheckHackrfTiming()
    {
        var wanted = Convert.FromHexString(Identification);
        for (int offset = 0; offset < 4; offset++)
        {
            byte[] iq = new byte[2*(150 + 240*4)];
            void Pulse(int halfBit)
            {
                for (int k = 0; k < 4; k++)
                {
                    int p = 2*(50 + offset + 4*halfBit+k);
                    iq[p] = unchecked((byte)-61);
                    iq[p+1] = 42;
                }
            }
            foreach (int bit in new[]{0,2,7,9}) Pulse(bit);
            for (int bit = 0; bit < 112; bit++)
                Pulse(16+2*bit+(((wanted[bit/8] >> (7-bit%8)) & 1) == 0 ? 1 : 0));
            int matches = 0;
            var channel = new HackrfChannel();
            var decoder = new AdsbDecoder(frame => matches += frame.AsSpan().SequenceEqual(wanted) ? 1 : 100, 2);
            for (int i = 0; i < iq.Length; i += 37)
                decoder.Feed(channel.Convert(iq.AsSpan(i, Math.Min(37,iq.Length-i))));
            if (matches != 1) return false;
        }
        return true;
    }

    internal static int CheckLiveHackrf()
    {
        var traffic = new AdsbTraffic { Status = "Opening HackRF" };
        var tuner = new HackrfTuner(Path.ChangeExtension(AppPaths.Settings, ".hackrf-gain.json"));
        long bytes = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HackrfAdsb.Run(traffic, tuner, stop.Token, n => Interlocked.Add(ref bytes, n));
        var rows = traffic.Snapshot(Environment.TickCount64);
        Console.WriteLine($"{traffic.Status} · {bytes:N0} USB bytes · {traffic.Messages:N0} valid messages · {rows.Length} aircraft");
        return bytes == 0 ? 1 : traffic.Messages == 0 ? 2 : 0;
    }

    internal static int Replay(string path, bool hackrf = false)
    {
        using var source = File.OpenRead(path);
        var traffic = new AdsbTraffic();
        long bytes = 0;
        int bytesPerMs = hackrf ? 16000 : 4000;
        var channel = hackrf ? new HackrfChannel() : null;
        var decoder = new AdsbDecoder(f => traffic.Update(f,bytes/bytesPerMs), samplesPerHalfBit: hackrf ? 2 : 1);
        byte[] block = new byte[16381];
        int read;
        while ((read=source.Read(block))>0)
        {
            bytes += read;
            var input = block.AsSpan(0,read);
            if (channel == null) decoder.Feed(input);
            else decoder.Feed(channel.Convert(input));
        }
        var rows = traffic.Snapshot(bytes/bytesPerMs);
        Console.WriteLine($"{bytes} bytes, {traffic.Messages} checksum-valid DF17 messages, {rows.Length} recent aircraft, {rows.Count(r=>r.Position.HasValue)} positions");
        return traffic.Messages>0 ? 0 : 1;
    }
}
