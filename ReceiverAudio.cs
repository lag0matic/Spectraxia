namespace Spectraxia;

internal sealed class ReceiverAudio(int rate)
{
    private readonly FmChannel channel = new(rate);
    private readonly RfFilters filters = new(rate);
    internal float[]? FilteredIq { get; private set; }
    private readonly AudioAgc audioAgc = new();
    private AutoNotch? autoNotch;
    private AgcSpeed lastSpeed;
    private Wfm? fm;
    private NarrowReceiver? narrow;
    private ReceiverSettings? previous;
    private long lastStation = -1, lastCenter = -1;
    private int phase;
    internal double Level { get; private set; }
    internal bool Gate { get; private set; }
    internal long DecodedStation => lastStation;
    internal int TextGroups => fm?.Rds.TextGroups ?? 0;
    internal bool StereoLocked => fm?.StereoLocked == true;
    internal string StationName => fm?.Rds.Name ?? "";
    internal string RadioText => fm?.Rds.Text ?? "";

    internal short[] Decode(byte[] iq, long center, long station, ReceiverSettings active, int volume, Action<short[]>? packetAudio = null)
    {
        if (!TuningView.Fits(station, center, rate, active.Mode, active.Width))
            station = lastStation >= 0 && TuningView.Fits(lastStation, center, rate, active.Mode, active.Width) ? lastStation : center;
        bool retuned = lastStation != station || lastCenter != center;
        if (retuned || previous?.Mode != active.Mode || previous.Width != active.Width)
        {
            channel.ResetFilter();
            fm = active.Mode == ListenMode.WFM ? new Wfm(phase) : null;
            narrow = fm == null ? new NarrowReceiver(active.Mode, active.Width, phase) : null;
        }
        FilteredIq = filters.Process(iq, center, active);
        var baseband = channel.Extract(iq, station-center, fm != null ? active.WfmBandwidthKhz : 50, FilteredIq);
        var pcm = fm != null ? fm.Decode(baseband,true,active.ForceMono) : narrow!.Decode(baseband);
        if (active.Mode == ListenMode.NFM) packetAudio?.Invoke(pcm);
        phase = (phase + baseband.Length/2) % 5;
        Level = narrow?.Level ?? channel.LevelDbfs;
        Gate = active.PassSignal(Level, Gate && !retuned && previous?.SquelchEnabled == active.SquelchEnabled &&
            previous.SquelchDbfs == active.SquelchDbfs);
        var speed = active.AudioSpeed;
        if (Gate && active.AutoNotchHz > 0 && !active.BypassNotches && active.Mode is ListenMode.AM or ListenMode.USB or ListenMode.LSB)
        {
            if (retuned || previous?.Mode != active.Mode || previous.Width != active.Width) autoNotch = null;
            (autoNotch ??= new()).Apply(pcm, active.AutoNotchHz);
        }
        else autoNotch = null;
        if (retuned || previous?.Mode != active.Mode || speed != lastSpeed || !Gate) audioAgc.Reset();
        lastSpeed = speed;
        if (Gate && speed != AgcSpeed.Off)
            audioAgc.Apply(pcm, fm == null ? 1 : 2, speed);
        int gain = Gate ? volume : 0;
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(pcm[i] * gain / 100);
        lastCenter = center;
        lastStation = station;
        previous = active;
        return pcm;
    }
}
