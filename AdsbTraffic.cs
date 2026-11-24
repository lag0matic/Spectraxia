namespace Spectraxia;

internal readonly record struct GeoPosition(double Lat, double Lon);
internal sealed record AircraftRow(string Address, string Callsign, int? Altitude, int? Speed,
    double? Track, int? Climb, GeoPosition? Position, long Seen, int Messages, EmitterCategory Category = EmitterCategory.Unknown)
{
    internal long FirstSeen { get; init; }
    internal long? PositionSeen { get; init; }
}

internal sealed class AdsbTraffic
{
    private sealed record Cpr(double Lat, double Lon, long Time);
    private sealed class Aircraft(string address, long firstSeen)
    {
        internal AircraftRow Row = new(address, "", null, null, null, null, null, firstSeen, 0) { FirstSeen=firstSeen };
        internal Cpr? Even, Odd;
        internal long VelocityTime, AltitudeTime;
    }
    private readonly Dictionary<int, Aircraft> aircraft = new();
    private long messages;
    private readonly Queue<long> recent = new();
    internal volatile int[]? GainSteps;
    internal volatile string Status = "Switching to 1090 MHz";
    internal long Messages => Interlocked.Read(ref messages);

    // Called only with CRC-verified DF17 messages.
    internal void Update(byte[] frame, long now)
    {
        int address = Bits(frame, 9, 24);
        int type = Bits(frame, 33, 5);
        lock (aircraft)
        {
            var a = GetAircraft(address, now);
            var row = a.Row with { Seen = now, Messages = a.Row.Messages + 1 };
            if (type is >= 1 and <= 4)
            {
                Span<char> id = stackalloc char[8];
                for (int n = 0; n < id.Length; n++)
                {
                    int symbol = Bits(frame, 41+n*6, 6);
                    id[n] = symbol is >= 1 and <= 26 ? (char)('A'+symbol-1) :
                        symbol is >= 48 and <= 57 ? (char)symbol : ' ';
                }
                row = row with { Callsign = new string(id).Trim(), Category = AircraftSymbol.Decode(type, Bits(frame,38,3)) };
            }
            else if (type is >= 9 and <= 18)
            {
                a.AltitudeTime = now;
                int alt = Bits(frame, 41, 12);
                int? feet = (alt & 16) != 0 ? (((alt >> 1) & ~15) | (alt & 15))*25-1000 : null;
                row = row with { Altitude = feet };
                bool odd = Bits(frame, 54, 1) != 0;
                var fix = new Cpr(Bits(frame,55,17)/131072.0, Bits(frame,72,17)/131072.0, now);
                if (odd) a.Odd = fix; else a.Even = fix;
                if (a.Even is {} e && a.Odd is {} o && Math.Abs(e.Time-o.Time) <= 10_000 &&
                    Locate(e, o, odd) is {} position)
                {
                    row = row with { Position = position, PositionSeen = now };
                }
            }
            else if (type == 19)
            {
                a.VelocityTime = now;
                int raw = Bits(frame, 70, 9);
                row = row with { Climb = raw == 0 ? null : (raw-1)*64*(Bits(frame,69,1)==0 ? 1 : -1) };
                int subtype = Bits(frame,38,3);
                if (subtype is 1 or 2)
                {
                    int x = Bits(frame,47,10), y = Bits(frame,58,10);
                    if (x == 0 || y == 0) row = row with { Speed = null, Track = null };
                    else
                    {
                        x = (x-1)*(Bits(frame,46,1)==0 ? 1 : -1);
                        y = (y-1)*(Bits(frame,57,1)==0 ? 1 : -1);
                        row = row with { Speed = (int)Math.Round(Math.Sqrt(x*x+y*y)*(subtype==2 ? 4 : 1)),
                            Track = x==0 && y==0 ? null : (Math.Atan2(x,y)*180/Math.PI+360)%360 };
                    }
                }
                else row = row with { Speed = null, Track = null };
            }
            else if (type is >= 5 and <= 8)
            {
                a.Even = a.Odd = null;
                row = row with { Position = null, PositionSeen = null, Altitude = null, Speed = null, Track = null, Climb = null };
            }
            Record(a, row, now);
        }
    }

    internal void Update(SbsReport report, long now)
    {
        lock (aircraft)
        {
            var a = GetAircraft(report.Address, now);
            var prior = a.Row;
            if (report.Altitude.HasValue) a.AltitudeTime = now;
            if (report.Speed.HasValue || report.Track.HasValue || report.Climb.HasValue) a.VelocityTime = now;
            Record(a, prior with {
                Callsign = report.Callsign.Length > 0 ? report.Callsign : prior.Callsign,
                Altitude = report.Altitude ?? prior.Altitude, Speed = report.Speed ?? prior.Speed,
                Track = report.Track ?? prior.Track, Climb = report.Climb ?? prior.Climb,
                Position = report.Position ?? prior.Position, PositionSeen = report.Position.HasValue ? now : prior.PositionSeen,
                Messages = prior.Messages + 1, Seen = now
            }, now);
        }
    }

    private Aircraft GetAircraft(int address, long now)
    {
        if (!aircraft.TryGetValue(address, out var a) || now - a.Row.Seen > 120_000)
            aircraft[address] = a = new Aircraft(address.ToString("X6"), now);
        return a;
    }

    internal void SetCategory(int address, EmitterCategory category, long now)
    {
        lock (aircraft)
        {
            var a = GetAircraft(address, now);
            a.Row = a.Row with { Category = category };
        }
    }

    private void Record(Aircraft a, AircraftRow row, long now)
    {
        a.Row = row;
        Interlocked.Increment(ref messages);
        ExpireMessages(now);
        recent.Enqueue(now);
    }
    private void ExpireMessages(long now)
    {
        while (recent.TryPeek(out long time) && now - time >= 10000) recent.Dequeue();
    }
    internal int RecentMessages(long now)
    {
        lock (aircraft)
        {
            ExpireMessages(now);
            return recent.Count;
        }
    }

    internal AircraftRow[] Snapshot(long now)
    {
        lock (aircraft)
        {
            foreach (var key in aircraft.Where(p => now-p.Value.Row.Seen > 120_000).Select(p=>p.Key).ToArray())
                aircraft.Remove(key);
            return aircraft.Values.Where(a => a.Row.Messages > 0).Select(a =>
            {
                var row = a.Row;
                if (now-a.Row.PositionSeen > 60_000) row = row with { Position=null, PositionSeen=null };
                if (now-a.VelocityTime > 60_000) row = row with { Speed=null, Track=null, Climb=null };
                if (now-a.AltitudeTime > 60_000) row = row with { Altitude=null };
                return row;
            }).OrderBy(a=>a.Address).ToArray();
        }
    }

    private static int Bits(byte[] data, int first, int count)
    {
        int value = 0;
        for (int i = first-1; i < first-1+count; i++)
            value = (value<<1) | ((data[i/8] >> (7-i%8)) & 1);
        return value;
    }

    private static int Mod(int a, int b) => (a % b + b) % b;
    private static int Zones(double latitude)
    {
        if (Math.Abs(latitude) >= 87) return Math.Abs(latitude)==87 ? 2 : 1;
        double c = Math.Cos(latitude*Math.PI/180);
        return (int)Math.Floor(2*Math.PI / Math.Acos(1 - (1-Math.Cos(Math.PI/30))/(c*c)));
    }

    private static GeoPosition? Locate(Cpr even, Cpr odd, bool newestOdd)
    {
        int j = (int)Math.Floor(59*even.Lat - 60*odd.Lat + .5);
        double a = 6*(Mod(j,60)+even.Lat), b = 360.0/59*(Mod(j,59)+odd.Lat);
        if (a >= 270) a -= 360;
        if (b >= 270) b -= 360;
        if (Math.Abs(a)>90 || Math.Abs(b)>90 || Zones(a)!=Zones(b)) return null;
        int nl = Zones(a), n = Math.Max(1, nl-(newestOdd ? 1 : 0));
        int m = (int)Math.Floor(even.Lon*(nl-1) - odd.Lon*nl + .5);
        double lon = 360.0/n * (Mod(m,n)+(newestOdd ? odd.Lon : even.Lon));
        return new(newestOdd ? b : a, lon >= 180 ? lon-360 : lon);
    }
}
