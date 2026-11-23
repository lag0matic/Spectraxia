namespace Spectraxia;

// Advances only on settled samples from the current dial. UI thread owns the scan.
internal sealed class FavoriteScanner
{
    private readonly StationMemory[] stations;
    private readonly TuningView tuning;
    private int index, consecutive;
    private long entered = -1, lastOpen = -1;
    internal int Threshold { get; set; }
    internal int DelayMs { get; set; } = 1500;
    internal bool Open { get; private set; }
    internal bool Held { get; private set; }
    internal StationMemory Current => stations[index];
    internal ScanTarget Target => new(tuning.CaptureCenter, Current.Dial.Hz, Open, Current.Dial, Threshold);
    internal string Status => $"{(Held ? "Holding" : Open ? "Receiving" : lastOpen >= 0 ? "Waiting for reply" : "Scanning")}  {Current.Name}  ·  {index+1}/{stations.Length}";

    internal FavoriteScanner(StationMemory[] stations, int rate, int threshold)
    {
        if (stations.Length == 0) throw new ArgumentException("Select a favorite to scan.");
        this.stations = stations;
        Threshold = threshold;
        tuning = new(stations[0].Dial.Hz);
        tuning.ChangeCapture(rate);
        tuning.Zoom(rate / 2);
        Tune();
    }

    private void Tune()
    {
        var dial = Current.Dial;
        tuning.Mode = dial.Mode;
        tuning.FilterHz = dial.Width;
        tuning.Tune(dial.Hz, false);
        Open = false;
        entered = lastOpen = -1;
        consecutive = 0;
    }
    internal void Skip()
    {
        index = (index + 1) % stations.Length;
        Held = false;
        Tune();
    }
    internal void ToggleHold() => Held = !Held;
    internal void Observe(long center, bool gate, long now)
    {
        if (center != tuning.CaptureCenter) return;
        if (entered < 0) entered = now;
        consecutive = gate ? Math.Min(2, consecutive + 1) : 0;
        Open = gate && (lastOpen >= 0 || consecutive >= 2);
        if (Open) lastOpen = now;
        else if (!Held && !gate && (lastOpen >= 0 ? now-lastOpen >= DelayMs : now-entered >= 180)) Skip();
    }
}
