namespace Spectraxia;

internal sealed class NotchPanel : FlowLayoutPanel
{
    private readonly List<ManualNotch> notches;
    private readonly Stack<ManualNotch> removed = new();
    private readonly ComboBox list = new() { AccessibleName = "Saved notches", Width = 212, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox width = new() { AccessibleName = "Notch width", Width = 145, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox enabled = new() { Text = "Enabled", AutoSize = true };
    private readonly CheckBox bypass = new() { Text = "Bypass all notches", AutoSize = true };
    private readonly Button tune = Button("Tune"), remove = Button("Remove"), undo = Button("Undo removal");
    private readonly ContextMenuStrip menu = new();
    private readonly Spectrum plot;
    private readonly Action<ManualNotch[], bool> save;
    private static Button Button(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private ManualNotch? Selected => list.SelectedItem as ManualNotch;

    internal NotchPanel(ReceiverSettings settings, Spectrum plot, Action<ManualNotch[], bool> save, Action<long> tuneTo)
    {
        this.plot = plot;
        this.save = save;
        notches = (settings.Notches ?? []).OrderBy(n => n.Hz).ToList();
        bypass.Checked = settings.BypassNotches;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Margin = Padding.Empty;
        var toggle = new CheckBox { Text = "Manual notches", Appearance = Appearance.Button, AutoSize = true };
        var editor = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MaximumSize = new Size(218, 0), Margin = Padding.Empty, Visible = false };
        Controls.Add(toggle);
        Controls.Add(editor);
        toggle.CheckedChanged += (_, _) => editor.Visible = toggle.Checked;
        var atDial = Button("Add at dial");
        width.Items.AddRange(ManualNotch.Widths.Select(ManualNotch.WidthName).ToArray());
        editor.Controls.AddRange([list, enabled, width, atDial, tune, remove, undo, bypass]);
        list.SelectedIndexChanged += (_, _) => ShowSelection();
        enabled.Click += (_, _) => { if (Selected is { } n) Put(n with { Enabled = enabled.Checked }); };
        width.SelectionChangeCommitted += (_, _) =>
        {
            if (Selected is { } n) Put(n with { WidthHz = ManualNotch.Widths[width.SelectedIndex] });
        };
        atDial.Click += (_, _) =>
        {
            long hz = plot.View.Station;
            Put(notches.Find(n => n.Hz == hz) ?? new ManualNotch(hz));
        };
        tune.Click += (_, _) => { if (Selected is { } n) tuneTo(n.Hz); };
        remove.Click += (_, _) => { if (Selected is { } n) Remove(n); };
        undo.Click += (_, _) => { if (removed.TryPop(out var n)) Put(n); };
        bypass.CheckedChanged += (_, _) => Publish();
        plot.MouseUp += PlotMouseUp;
        RefreshList(null);
        plot.Notches = notches.ToArray();
        plot.BypassNotches = bypass.Checked;
    }

    private void ShowSelection()
    {
        while (removed.TryPeek(out var restored) && notches.Any(n => n.Hz == restored.Hz)) removed.Pop();
        var n = Selected;
        enabled.Enabled = width.Enabled = tune.Enabled = remove.Enabled = n != null;
        enabled.Checked = n?.Enabled == true;
        width.SelectedIndex = n == null ? -1 : Array.IndexOf(ManualNotch.Widths, n.WidthHz);
        undo.Enabled = removed.Count > 0;
    }
    private void RefreshList(long? select)
    {
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(notches.ToArray());
        if (list.Items.Count > 0) list.SelectedIndex = Math.Max(0, notches.FindIndex(n => n.Hz == select));
        list.EndUpdate();
        ShowSelection();
    }
    private void Publish()
    {
        plot.Notches = notches.ToArray();
        plot.BypassNotches = bypass.Checked;
        save(plot.Notches, bypass.Checked);
        plot.Invalidate();
    }
    private void Put(ManualNotch notch)
    {
        notches.RemoveAll(n => n.Hz == notch.Hz);
        notches.Add(notch);
        notches.Sort((a, b) => a.Hz.CompareTo(b.Hz));
        RefreshList(notch.Hz);
        Publish();
    }
    private void Remove(ManualNotch n)
    {
        notches.Remove(n);
        removed.Push(n);
        RefreshList(null);
        Publish();
    }
    private void PlotMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || plot.Width < 2) return;
        long hz = plot.View.FrequencyAt(Math.Clamp(e.X / (double)(plot.Width - 1), 0, 1));
        var nearest = notches.Where(n => n.Hz >= plot.View.Left && n.Hz <= plot.View.Left + plot.View.Span)
            .MinBy(n => Math.Abs(n.Hz - hz));
        var hit = nearest != null && Math.Abs(nearest.Hz - hz) <= Math.Max(nearest.WidthHz / 2,
            5.0 * plot.View.Span / (plot.Width - 1)) ? nearest : null;
        while (menu.Items.Count > 0) menu.Items[0].Dispose();
        menu.Items.Add(new ToolStripMenuItem($"{(hit == null ? "Add notch" : "Notch width")} · {(hit?.Hz ?? hz) / 1e6:F6} MHz") { Enabled = false });
        foreach (int w in ManualNotch.Widths)
        {
            var item = new ToolStripMenuItem(ManualNotch.WidthName(w)) { Checked = hit?.WidthHz == w };
            item.Click += (_, _) => Put((hit ?? new ManualNotch(hz)) with { WidthHz = w });
            menu.Items.Add(item);
        }
        if (hit != null)
        {
            menu.Items.Add(hit.Enabled ? "Disable notch" : "Enable notch", null, (_, _) => Put(hit with { Enabled = !hit.Enabled }));
            menu.Items.Add("Remove notch", null, (_, _) => Remove(hit));
        }
        menu.Show(plot, e.Location);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { plot.MouseUp -= PlotMouseUp; menu.Dispose(); }
        base.Dispose(disposing);
    }
}
