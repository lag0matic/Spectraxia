using System.Text.Json;

namespace Spectraxia;

internal sealed class AirScanPanel : FlowLayoutPanel
{
    private readonly NumericUpDown low = Number(118, 137, 118, .025m, 6);
    private readonly NumericUpDown high = Number(118, 137, 136.975m, .025m, 6);
    private readonly NumericUpDown delay = Number(0, 10, 1.5m, .25m, 2);
    private readonly NumericUpDown threshold = Number(6, 40, 14, 1, 0);
    private readonly ComboBox spacing = new() { Width = 102, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button start = Button("Start"), hold = Button("Hold"), skip = Button("Skip"), lockout = Button("Lock out"), clear = Button("Clear locks");
    private readonly Label state = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly HashSet<long> locks = [];
    private readonly string lockFile;
    internal AirScanner? Scan { get; private set; }
    internal FavoriteScanner? Favorites { get; private set; }
    internal ScanTarget? Target => Favorites?.Target ?? Scan?.Target;
    private readonly NumericUpDown favoriteLevel = Number(-95, 0, -55, 1, 0);
    private readonly List<Control> airFields = [];
    private readonly List<Control> favoriteFields = [];
    private readonly Func<int> sampleRate;
    internal bool CanStart => Enabled && start.Enabled;
    internal event Action? Changed;

    internal AirScanPanel(string lockFile, Func<int> rate)
    {
        this.lockFile = lockFile;
        sampleRate = rate;
        Dock = DockStyle.Top;
        AutoSize = true;
        Padding = new Padding(5);
        Visible = false;
        try
        {
            if (File.Exists(lockFile))
                locks.UnionWith(JsonSerializer.Deserialize<long[]>(File.ReadAllText(lockFile)) ?? []);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { state.Text = "Could not read scanner lockouts"; }
        spacing.Items.AddRange(["25 kHz","8.33 kHz"]);
        spacing.SelectedIndex = 0;
        AddField("From MHz", low);
        AddField("To MHz", high);
        AddField("Spacing", spacing);
        AddField("Trigger dB above noise", threshold);
        airFields.AddRange(Controls.Cast<Control>());
        AddField("Squelch dBFS", favoriteLevel);
        favoriteFields.AddRange(Controls.Cast<Control>().Skip(airFields.Count));
        AddField("Reply delay (s)", delay);
        Controls.AddRange([start, hold, skip, lockout, clear, state]);
        start.Click += (_, _) =>
        {
            if (Target != null) { Stop(); return; }
            try
            {
                Scan = new((long)(low.Value * 1e6m), (long)(high.Value * 1e6m), spacing.SelectedIndex == 1, rate(), locks);
                ApplyLevels();
                UpdateState();
                Changed?.Invoke();
            }
            catch (ArgumentException ex) { state.Text = ex.Message; }
        };
        delay.ValueChanged += (_, _) => ApplyLevels();
        threshold.ValueChanged += (_, _) => ApplyLevels();
        favoriteLevel.ValueChanged += (_, _) =>
        {
            if (Favorites == null) return;
            Favorites.Threshold = (int)favoriteLevel.Value;
            Publish();
        };
        hold.Click += (_, _) => { Favorites?.ToggleHold(); Scan?.ToggleHold(); Publish(); };
        skip.Click += (_, _) => { Favorites?.Skip(); Scan?.Skip(Environment.TickCount64, false); Publish(); };
        lockout.Click += (_, _) => { Scan?.Skip(Environment.TickCount64, true); Publish(); SaveLocks(); };
        clear.Click += (_, _) => { locks.Clear(); Publish(); SaveLocks(); };
        UpdateState();
    }
    private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal step, int places) =>
        new() { Minimum = min, Maximum = max, DecimalPlaces = places, Increment = step, Value = value, Width = places == 6 ? 115 : 64 };
    private static Button Button(string label) => new() { Text = label, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private void AddField(string label, Control field)
    {
        field.AccessibleName = label;
        Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
        Controls.Add(field);
    }
    private void SaveLocks()
    {
        string temp = lockFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(locks.Order()));
            File.Move(temp, lockFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { state.Text = "Lockouts apply this session. Could not save them."; }
    }
    internal void StartFavorites(StationMemory[] stations, int threshold)
    {
        if (!CanStart || stations.Length == 0) return;
        Scan = null;
        favoriteLevel.Value = threshold;
        Favorites = new(stations, sampleRate(), threshold) { DelayMs = (int)(delay.Value * 1000) };
        Publish();
    }
    internal void Stop()
    {
        if (Target == null) return;
        Favorites = null;
        Scan = null;
        state.Text = "";
        Publish();
    }
    internal void SetAvailable(bool live) => start.Enabled = live || Target != null;
    private void ApplyLevels()
    {
        if (Favorites != null) Favorites.DelayMs = (int)(delay.Value * 1000);
        if (Scan == null) return;
        Scan.DelayMs = (int)(delay.Value * 1000);
        Scan.Threshold = (int)threshold.Value;
    }
    internal void Observe(long center, float[] bins, long now, bool gate = false)
    {
        if (Favorites is {} favorites)
        {
            var prior = favorites.Target;
            var priorStation = favorites.Current.Id;
            favorites.Observe(center, gate, now);
            if (favorites.Target != prior || favorites.Current.Id != priorStation) Publish();
            return;
        }
        if (Scan == null) return;
        var before = Scan.Target;
        var channel = Scan.Channel;
        Scan.Observe(center, bins, now);
        if (Scan.Target != before || Scan.Channel != channel) Publish();
    }
    private void Publish() { UpdateState(); Changed?.Invoke(); }
    private void UpdateState()
    {
        foreach (var field in airFields) field.Visible = Favorites == null;
        foreach (var field in favoriteFields) field.Visible = Favorites != null;
        lockout.Visible = clear.Visible = Favorites == null;
        if (Favorites is {} favorite)
        {
            start.Text = "Stop";
            hold.Enabled = skip.Enabled = true;
            hold.Text = favorite.Held ? "Release hold" : "Hold";
            state.Text = favorite.Status;
            return;
        }
        low.Enabled = high.Enabled = spacing.Enabled = Scan == null;
        hold.Enabled = skip.Enabled = lockout.Enabled = Scan?.Channel != null;
        hold.Text = Scan?.Held == true ? "Release hold" : "Hold";
        start.Text = Scan == null ? "Start" : "Stop";
        clear.Enabled = locks.Count > 0;
        clear.Text = locks.Count > 0 ? $"Clear locks ({locks.Count})" : "Clear locks";
        if (Scan == null) return;
        state.Text = Scan.Channel is long hz
            ? $"{(Scan.Held ? "Holding" : Scan.Signal ? "Receiving" : "Waiting for reply")}  {hz/1e6:F6} MHz AM"
            : $"Scanning {Scan.FirstHz/1e6:F3}–{Scan.LastHz/1e6:F3} MHz  ({Scan.SliceNumber}/{Scan.SliceCount})";
    }
}
