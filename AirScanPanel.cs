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
    internal event Action? Changed;

    internal AirScanPanel(string lockFile, Func<int> rate)
    {
        this.lockFile = lockFile;
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
        AddField("Reply delay (s)", delay);
        Controls.AddRange([start, hold, skip, lockout, clear, state]);
        start.Click += (_, _) =>
        {
            if (Scan != null) { Stop(); return; }
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
        hold.Click += (_, _) => { Scan?.ToggleHold(); Publish(); };
        skip.Click += (_, _) => { Scan?.Skip(Environment.TickCount64, false); Publish(); };
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
    internal void Stop()
    {
        if (Scan == null) return;
        Scan = null;
        state.Text = "";
        Publish();
    }
    internal void SetAvailable(bool live) => start.Enabled = live || Scan != null;
    private void ApplyLevels()
    {
        if (Scan == null) return;
        Scan.DelayMs = (int)(delay.Value * 1000);
        Scan.Threshold = (int)threshold.Value;
    }
    internal void Observe(long center, float[] bins, long now)
    {
        if (Scan == null) return;
        var before = Scan.Target;
        var channel = Scan.Channel;
        Scan.Observe(center, bins, now);
        if (Scan.Target != before || Scan.Channel != channel) Publish();
    }
    private void Publish() { UpdateState(); Changed?.Invoke(); }
    private void UpdateState()
    {
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
