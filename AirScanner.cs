namespace Spectraxia;

internal sealed record ScanTarget(long Center, long Station, bool Audible, DialSetting? Favorite = null, int Threshold = -55)
{
    internal ReceiverSettings Listen(ReceiverSettings normal) => Favorite is {} dial
        ? dial.Apply(normal) with { SquelchEnabled = true, SquelchDbfs = Threshold }
        : normal with
        { Mode = ListenMode.AM, FilterHz = 8000, SquelchEnabled = false };
    internal void Gate(short[] pcm, bool settled, long actualCenter)
    {
        if (!Audible || !settled || Center != actualCenter) Array.Clear(pcm);
    }
}

// Owned by the UI thread. Only immutable targets cross into the radio callback.
internal sealed class AirScanner
{
    internal const int MinHz = 118000000, MaxHz = 136991667;
    private const int Anchor = MinHz;
    private readonly int divisions, first, last, perSlice, rate;
    private readonly HashSet<long> locks;
    private readonly Dictionary<long, long> skipped = [];
    private readonly HashSet<long> previous = [];
    private int slice;
    private long entered = -1, lastSignal;
    internal long? Channel { get; private set; }
    internal bool Held { get; private set; }
    internal bool Signal { get; private set; }
    internal int Threshold { get; set; } = 14;
    internal int DelayMs { get; set; } = 1500;
    internal long FirstHz => Hz(slice);
    internal long LastHz => Hz(Math.Min(last, slice + perSlice - 1));
    internal long Center => Hz(slice + (Math.Min(perSlice, last - slice + 1) / 2)) - (long)Math.Round(12500.0 / divisions);
    internal ScanTarget Target => new(Center, Channel ?? FirstHz, Channel.HasValue && Signal);
    internal int SliceCount => (last - first) / perSlice + 1;
    internal int SliceNumber => (slice - first) / perSlice + 1;

    internal AirScanner(long low, long high, bool narrowGrid, int rate, HashSet<long> locks)
    {
        if (low < MinHz || high > 137000000 || low > high || !Radio.SampleRates.Contains(rate))
            throw new ArgumentException("Choose an ordered range inside 118–137 MHz.");
        divisions = narrowGrid ? 3 : 1;
        // Bounds are RF carrier frequencies, rounded to the nearest whole Hz.
        first = Math.Max(0, (int)Math.Floor((low - Anchor) * divisions / 25000.0));
        if (Hz(first) < low) first++;
        last = (int)Math.Ceiling((Math.Min(high, MaxHz) - Anchor) * divisions / 25000.0);
        if (Hz(last) > Math.Min(high, MaxHz)) last--;
        if (last < first) throw new ArgumentException("That range contains no channel on this spacing.");
        this.rate = rate;
        this.locks = locks;
        perSlice = rate * 4 / 5 * divisions / 25000;
        slice = first;
    }

    private long Hz(int index) => Anchor + (index * 25000L + divisions / 2) / divisions;

    private double Contrast(float[] bins, long carrier)
    {
        int bin = (int)Math.Round((carrier - Center) * bins.Length / (double)rate) + bins.Length / 2;
        int width = Math.Max(1, (int)Math.Round(1700.0 * bins.Length / rate));
        float peak = bins[bin];
        for (int i = 1; i <= width; i++) peak = Math.Max(peak, Math.Max(bins[bin-i], bins[bin+i]));
        // Sample across both shoulders, leaving a guard around the AM carrier.
        Span<float> floor = stackalloc float[16];
        for (int i = 0; i < 8; i++)
        {
            int delta = Math.Max(width + 1, (int)Math.Round((3600 + 850 * i) * bins.Length / (double)rate));
            floor[2*i] = bins[bin-delta];
            floor[2*i+1] = bins[bin+delta];
        }
        floor.Sort();
        return peak - (floor[7] + floor[8]) / 2;
    }

    internal void Observe(long center, float[] bins, long now)
    {
        if (center != Center) return;
        if (entered < 0) entered = now;
        if (Channel is long tuned)
        {
            Signal = Contrast(bins, tuned) >= Threshold - 3;
            if (Signal) lastSignal = now;
            else if (!Held && now - lastSignal >= DelayMs) Release();
            return;
        }

        long? winner = null;
        double strongest = double.NegativeInfinity;
        bool pending = false;
        for (int i = slice; i <= Math.Min(slice + perSlice - 1, last); i++)
        {
            long hz = Hz(i);
            if (locks.Contains(hz) || skipped.GetValueOrDefault(hz, -1) > now) continue;
            double strength = Contrast(bins, hz);
            if (strength < Threshold) { previous.Remove(hz); continue; }
            if (previous.Add(hz)) { pending = true; continue; }
            if (strength > strongest) { strongest = strength; winner = hz; }
        }
        if (winner.HasValue)
        {
            Channel = winner;
            Signal = true;
            lastSignal = now;
            previous.Clear();
        }
        else if (now - entered >= 150 && (!pending || now - entered >= 250))
        {
            slice = slice + perSlice > last ? first : slice + perSlice;
            entered = -1;
            previous.Clear();
        }
    }

    internal void ToggleHold() { if (Channel.HasValue) Held = !Held; }
    internal void Skip(long now, bool lockOut)
    {
        if (Channel is not long hz) return;
        if (lockOut) locks.Add(hz);
        else skipped[hz] = now + 15000;
        Release();
    }
    private void Release()
    {
        Channel = null;
        Signal = Held = false;
        entered = -1;
        previous.Clear();
    }
}
