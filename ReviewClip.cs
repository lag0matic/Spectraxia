namespace Spectraxia;

internal sealed class ReviewClip
{
    internal readonly TapeBlock[] Blocks;
    internal readonly double[] Times;
    internal double Duration => Times[^1];
    internal ReviewClip(TapeBlock[] blocks)
    {
        if (blocks.Length == 0) throw new InvalidOperationException("No received samples to rewind.");
        Blocks = blocks;
        Times = new double[blocks.Length + 1];
        for (int i = 0; i < blocks.Length; i++) Times[i + 1] = Times[i] + blocks[i].Seconds;
    }
    internal int Index(double seconds)
    {
        int i = Array.BinarySearch(Times, seconds);
        return Math.Clamp(i < 0 ? ~i - 1 : i, 0, Blocks.Length - 1);
    }

    // Called on the playback worker. Start loading the next page while the last
    // second plays, keeping one decoder across the boundary.
    internal IEnumerable<(ReviewClip Window, int Page, int Index)> From(int page, int index,
        RecordingSource? source, CancellationToken token)
    {
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ReviewClip>? ahead = null;
        var window = this;
        try
        {
            while (true)
            {
                for (; index < window.Blocks.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (ahead == null && source != null && page+1 < source.Pages && window.Duration-window.Times[index] < 1)
                    {
                        int next = page+1;
                        ahead = Task.Run(() => new ReviewClip(source.Page(next, reading.Token)), reading.Token);
                    }
                    yield return (window, page, index);
                }
                if (ahead == null) yield break;
                var ready = ahead;
                ahead = null;
                window = ready.GetAwaiter().GetResult();
                page++;
                index = 0;
            }
        }
        finally
        {
            if (ahead != null)
            {
                reading.Cancel();
                try { ahead.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            }
        }
    }
}

internal sealed class ReviewDecoder
{
    private ReceiverAudio? decoder;
    private CaptureBand? previous;
    internal static DialSetting Dial(TapeBlock b, long? frequency, ListenMode? mode)
    {
        var meta = b.Band;
        var m = mode ?? meta.Mode;
        int width = m == meta.Mode ? meta.Width : Listening.Range(m).Default;
        long station = frequency ?? meta.Station;
        if (!TuningView.Fits(station, meta.Center, meta.Rate, m, width))
            station = TuningView.Fits(meta.Station, meta.Center, meta.Rate, m, width) ? meta.Station : meta.Center;
        return new(station, m, width);
    }
    internal short[] Stereo(TapeBlock b, bool recordedAudio, long? frequency, ListenMode? mode, ReceiverSettings settings)
    {
        short[] pcm;
        int channels;
        if (recordedAudio && b.Pcm.Length > 0 || b.Iq.Length == 0)
        {
            pcm = b.Pcm; channels = b.Channels;
        }
        else
        {
            if (previous?.Session != b.Band.Session || previous.Rate != b.Band.Rate || previous.Settled != b.Band.Settled)
                decoder = new ReceiverAudio(b.Band.Rate);
            previous = b.Band;
            var dial = Dial(b, frequency, mode);
            var active = dial.Apply(settings) with { SampleRate = b.Band.Rate, SquelchEnabled = false };
            pcm = decoder!.Decode(b.Iq, b.Band.Center, dial.Hz, active, 100);
            if (!b.Band.Settled) Array.Clear(pcm);
            channels = dial.Mode is ListenMode.WFM or ListenMode.IQ ? 2 : 1;
        }
        if (channels == 2) return pcm;
        var stereo = new short[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++) stereo[2 * i] = stereo[2 * i + 1] = pcm[i];
        return stereo;
    }
}
