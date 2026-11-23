namespace Spectraxia;

internal sealed class StationPanel : FlowLayoutPanel
{
    private readonly FrequencyLibrary library;
    private readonly string path;
    private readonly Func<DialSetting> current;
    private readonly ComboBox band = Picker("Band");
    private FavoritesWindow? favorites;
    private readonly Label error = new() { AutoSize = true, MaximumSize = new Size(212, 0), ForeColor = Color.Salmon, Visible = false };
    private bool writable = true;
    private bool dirty;

    private static ComboBox Picker(string name) => new()
    {
        Width = 212, AccessibleName = name, DropDownStyle = ComboBoxStyle.DropDownList
    };

    internal StationPanel(string path, Func<DialSetting> current, Func<string> decodedName,
        Action<DialSetting> recall, Spectrum plot, Func<StationMemory[],bool> scan)
    {
        this.path = path;
        this.current = current;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Margin = Padding.Empty;
        try { library = FrequencyLibrary.Load(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            library = new();
            writable = false;
            ShowError("Could not load the station library. " + ex.Message);
        }
        Controls.Add(new Label { AutoSize = true, Text = "Band" });
        band.Items.Add("Outside listed bands");
        band.Items.AddRange(BandPlan.Choices.ToArray());
        band.DropDown += (_, _) => band.DropDownWidth = Math.Max(band.Width,
            BandPlan.Choices.Max(label => TextRenderer.MeasureText(label, band.Font).Width) + SystemInformation.VerticalScrollBarWidth + 8);
        band.SelectionChangeCommitted += (_, _) =>
        {
            if (band.SelectedIndex > 0) recall(library.Recall((string)band.SelectedItem!));
            else TrackDial();
        };
        Controls.Add(band);
        var open = new Button { Text = "Favorites…", AutoSize = true };
        Controls.Add(open);
        Controls.Add(error);
        void RefreshPlot()
        {
            plot.Memories = library.Memories.OrderBy(m => m.Dial.Hz).ToArray();
            plot.Invalidate();
        }
        void Changed()
        {
            dirty = true;
            RefreshPlot();
            Flush();
        }
        open.Click += (_,_) =>
        {
            if (favorites == null || favorites.IsDisposed)
                favorites = new FavoritesWindow(library, current, decodedName, recall, Changed, scan, writable);
            if (!favorites.Visible) favorites.Show(FindForm());
            if (favorites.WindowState == FormWindowState.Minimized) favorites.WindowState = FormWindowState.Normal;
            favorites.Activate();
        };
        plot.RecallMemory += m =>
        {
            recall(m.Dial);
            favorites?.SelectStation(m.Id);
        };
        Disposed += (_,_) => favorites?.Dispose();
        RefreshPlot();
    }

    internal void TrackDial()
    {
        var dial = current() with { HdProgram = null };
        string? key = BandPlan.At(dial.Hz);
        band.SelectedIndex = key == null ? 0 : band.Items.IndexOf(key);
        if (key != null && (!library.Bands.TryGetValue(key, out var prior) || prior != dial))
        {
            library.Remember(dial);
            dirty = true;
        }
    }

    private void ShowError(string message)
    {
        error.Text = message;
        error.Visible = true;
    }

    internal void Flush()
    {
        if (!writable || !dirty) return;
        try
        {
            library.Save(path);
            dirty = false;
            error.Visible = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save station memories. " + ex.Message);
        }
    }
}
