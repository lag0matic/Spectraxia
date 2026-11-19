namespace Spectraxia;

internal sealed class ReviewPane : Panel
{
    private readonly ReviewMap map = new();
    private readonly TrackBar time = new() { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Width = 210, Height = 30, AutoSize = false, AccessibleName = "Recording time" };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 153, AccessibleName = "Playback mode" };
    private readonly NumericUpDown mhz = new() { DecimalPlaces = 6, Increment = .001m, Maximum = 1766, Width = 140, AccessibleName = "Playback frequency in MHz" };
    private readonly Label info = new() { AutoSize = true };
    private readonly Label unit = new() { Text = "MHz", AutoSize = true };
    private readonly Button play = MakeButton("Play"), prev = MakeButton("Previous 30 s"), next = MakeButton("Next 30 s");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 90 };
    private readonly Func<uint> endpoint;
    private readonly Func<int> volume;
    private readonly Func<ReceiverSettings> settings;
    private readonly Action<bool> reviewState;
    private ReviewClip? clip;
    private RecordingSource? source;
    private sealed record PlaybackCursor(ReviewClip Clip, int Page, int Index);
    private PlaybackCursor? playbackCursor;
    private int page, index, run, loadVersion;
    private bool playing, updating;
    private long? selectedHz;
    private CancellationTokenSource? stop;
    private Task worker = Task.CompletedTask;
    private string? playbackError;
    private string title = "";
    internal TapeBlock[]? PinnedLive => source == null ? clip?.Blocks : null;
    internal event Action? ClipChanged;
    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    internal ReviewPane(Func<uint> endpoint, Func<int> volume, Func<ReceiverSettings> settings, Action<bool> reviewState)
    {
        this.endpoint = endpoint; this.volume = volume; this.settings = settings; this.reviewState = reviewState;
        Dock = DockStyle.Fill; Visible = false;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(3) };
        var live = MakeButton("Return to live");
        Controls.Add(map); Controls.Add(bar);
        bar.Controls.AddRange([live, play, time, mode, mhz, unit, prev, next, info]);
        live.Click += (_, _) => LeaveReview();
        play.Click += (_, _) => { if (playing) Halt(); else { if (clip != null && index == clip.Blocks.Length - 1) index = 0; StartPlayback(); } };
        time.Scroll += (_, _) => Seek((clip?.Duration ?? 0) * time.Value / time.Maximum, selectedHz, false);
        map.Pick += (sec, hz) => Seek(sec, hz, hz.HasValue);
        mhz.ValueChanged += (_, _) => { if (!updating && clip != null) Seek(clip.Times[index], (long)(mhz.Value * 1000000), true); };
        mode.SelectionChangeCommitted += (_, _) => { if (playing) StartPlayback(); UpdateCursor(); };
        prev.Click += async (_, _) => await LoadPage(page - 1);
        next.Click += async (_, _) => await LoadPage(page + 1);
        timer.Tick += (_, _) =>
        {
            if (!playing || clip == null) return;
            if (Volatile.Read(ref playbackCursor) is { } cursor)
            {
                if (clip != cursor.Clip)
                {
                    clip = cursor.Clip; page = cursor.Page;
                    var display = settings();
                    map.Bind(clip, display.WaterfallMin, display.WaterfallMax);
                    prev.Enabled = page > 0; next.Enabled = source != null && page + 1 < source.Pages;
                }
                index = cursor.Index;
            }
            UpdateCursor();
            if (worker.IsCompleted)
            {
                Halt();
                if (playbackError != null) info.Text = playbackError;
            }
        };
    }
    internal void Pin(TapeBlock[] blocks)
    {
        var snapshot = new ReviewClip(blocks);
        loadVersion++; source = null; page = 0; title = "Rewind";
        Bind(snapshot);
    }
    internal async Task OpenFile(string path,CaptureBand? rawFormat=null)
    {
        int version = ++loadVersion;
        var input = await Task.Run(() => new RecordingSource(path,rawFormat));
        var snapshot = await Task.Run(() => new ReviewClip(input.Page(0)));
        if (version != loadVersion || IsDisposed || Disposing) return;
        source = input; title = Path.GetFileName(path) + (rawFormat==null?"":" · estimated clock"); page = 0;
        Bind(snapshot);
    }
    private async Task LoadPage(int target)
    {
        if (source is not { } input || target < 0 || target >= input.Pages) return;
        int version = ++loadVersion;
        try
        {
            var snapshot = await Task.Run(() => new ReviewClip(input.Page(target)));
            if (version != loadVersion || IsDisposed) return;
            page = target; Bind(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            if (version == loadVersion && !IsDisposed) { if (!Visible) throw; info.Text = ex.Message; }
        }
    }
    private void Bind(ReviewClip snapshot)
    {
        Halt(); clip = snapshot; index = 0; selectedHz = null;
        var first = snapshot.Blocks[0];
        mode.Items.Clear();
        if (first.Pcm.Length > 0) mode.Items.Add("Recorded audio");
        if (first.Iq.Length > 0)
        {
            mode.Items.Add("Recorded mode");
            foreach (var m in Enum.GetValues<ListenMode>()) mode.Items.Add(m);
        }
        mode.SelectedIndex = 0;
        mhz.Visible = unit.Visible = first.Iq.Length > 0;
        prev.Visible = next.Visible = source is { Pages: > 1 };
        prev.Enabled = page > 0; next.Enabled = source != null && page + 1 < source.Pages;
        var display = settings(); map.Bind(snapshot, display.WaterfallMin, display.WaterfallMax);
        reviewState(true); Visible = true; BringToFront(); UpdateCursor(); timer.Start();
        ClipChanged?.Invoke();
    }
    private void Seek(double seconds, long? hz, bool tune)
    {
        if (clip == null) return;
        index = clip.Index(seconds); selectedHz = hz;
        if (tune && Equals(mode.SelectedItem, "Recorded audio") && mode.Items.Contains("Recorded mode"))
            mode.SelectedItem = "Recorded mode";
        if (playing) StartPlayback();
        UpdateCursor();
    }
    private void UpdateCursor()
    {
        if (clip == null) return;
        var b = clip.Blocks[index];
        bool original = Equals(mode.SelectedItem, "Recorded audio");
        var dial = ReviewDecoder.Dial(b, original ? null : selectedHz, mode.SelectedItem as ListenMode?);
        updating = true;
        mhz.Value = dial.Hz / 1000000m;
        time.Value = Math.Clamp((int)Math.Round(clip.Times[index] / clip.Duration * 1000), 0, 1000);
        updating = false;
        map.Mark(index, dial.Hz);
        info.Text = $"{title}   {page * 30 + clip.Times[index]:0.0} s   {b.Utc.ToLocalTime():HH:mm:ss.fff}" +
            (b.Iq.Length > 0 ? $"   {Listening.Name(dial.Mode)}   RF center {b.Band.Center / 1e6:F6} MHz" : "") +
            (b.Band.Settled ? "" : "   tuner settling");
    }
    private void StartPlayback()
    {
        if (clip is not { } current) return;
        Halt();
        var prior = worker; var cancellation = new CancellationTokenSource(); stop = cancellation;
        var token = cancellation.Token; int generation = run, first = index;
        bool original = Equals(mode.SelectedItem, "Recorded audio");
        long? frequency = selectedHz; ListenMode? demod = mode.SelectedItem as ListenMode?;
        var active = settings(); uint output = endpoint();
        playing = true; play.Text = "Pause"; playbackError = null; playbackCursor = null;
        var input = source;
        int firstPage = page;
        worker = Task.Run(async () =>
        {
            try
            {
                await prior.ConfigureAwait(false); token.ThrowIfCancellationRequested();
                using var sink = new Headphones(output, true);
                var decoder = new ReviewDecoder();
                foreach (var (window, part, i) in current.From(firstPage,first,input,token))
                {
                    var samples = decoder.Stereo(window.Blocks[i], original, frequency, demod, active);
                    sink.WaitForRoom(token);
                    sink.Play(samples, volume());
                    if (Volatile.Read(ref run) == generation) Volatile.Write(ref playbackCursor, new(window, part, i));
                }
                sink.Drain(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (Volatile.Read(ref run) == generation) playbackError = ex.Message; }
        });
    }
    private void Halt()
    {
        Interlocked.Increment(ref run);
        if (stop != null)
        {
            var old = stop; old.Cancel(); stop = null;
            _ = worker.ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
        }
        playing = false; play.Text = "Play";
    }
    internal void LeaveReview()
    {
        loadVersion++; Halt(); timer.Stop(); Visible = false;
        map.Bind(null, -95, 0); clip = null; source = null; reviewState(false);
        playbackCursor = null; ClipChanged?.Invoke();
    }
    internal Task Shutdown() { LeaveReview(); return worker; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { loadVersion++; Halt(); timer.Dispose(); }
        base.Dispose(disposing);
    }
}
