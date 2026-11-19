namespace Spectraxia;

internal sealed class StationPanel : FlowLayoutPanel
{
    private readonly FrequencyLibrary library;
    private readonly string path;
    private readonly Func<DialSetting> current;
    private readonly ComboBox band = Picker("Band");
    private readonly ComboBox stations = Picker("Saved stations");
    private readonly Label error = new() { AutoSize = true, MaximumSize = new Size(212, 0), ForeColor = Color.Salmon, Visible = false };
    private bool writable = true;
    private bool dirty;

    private static ComboBox Picker(string name) => new()
    {
        Width = 212, AccessibleName = name, DropDownStyle = ComboBoxStyle.DropDownList
    };

    internal StationPanel(string path, Func<DialSetting> current, Func<string> decodedName,
        Action<DialSetting> recall, Spectrum plot)
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
        var manage = new CheckBox { Text = "Station memories", AutoSize = true, Appearance = Appearance.Button };
        Controls.Add(manage);
        Controls.Add(stations);
        var editor = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MaximumSize = new Size(218, 0), Margin = Padding.Empty, Visible = false, Enabled = writable };
        Controls.Add(editor);
        Controls.Add(error);
        var name = new TextBox { Width = 212, MaxLength = 80, AccessibleName = "Station name" };
        editor.Controls.Add(name);
        Button AddButton(string caption)
        {
            var button = new Button { Text = caption, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            editor.Controls.Add(button);
            return button;
        }
        var save = AddButton("Save new");
        var rename = AddButton("Rename");
        var update = AddButton("Update");
        var remove = AddButton("Remove");
        StationMemory? Selected() => stations.SelectedItem as StationMemory;
        void Suggest()
        {
            string ps = decodedName().Trim();
            name.PlaceholderText = ps.Length > 0 ? ps : $"{current().Hz / 1e6:0.######} MHz";
        }
        void SelectMemory(StationMemory? memory)
        {
            rename.Enabled = update.Enabled = remove.Enabled = memory != null;
            name.Clear();
            Suggest();
        }
        void RefreshList(Guid? selected)
        {
            var ordered = library.Memories.OrderBy(m => m.Dial.Hz).ThenBy(m => m.Name).ToArray();
            stations.BeginUpdate();
            stations.Items.Clear();
            stations.Items.AddRange(ordered);
            stations.SelectedItem = ordered.FirstOrDefault(m => m.Id == selected);
            stations.EndUpdate();
            stations.Visible = ordered.Length > 0;
            SelectMemory(Selected());
            plot.Memories = ordered;
            plot.Invalidate();
        }
        void Changed(Guid? selected)
        {
            dirty = true;
            RefreshList(selected);
            Flush();
        }
        manage.CheckedChanged += (_, _) =>
        {
            editor.Visible = manage.Checked;
            if (manage.Checked) Suggest();
        };
        name.Enter += (_, _) => Suggest();
        stations.SelectionChangeCommitted += (_, _) =>
        {
            if (Selected() is not { } m) return;
            recall(m.Dial);
            SelectMemory(m);
        };
        plot.RecallMemory += m =>
        {
            stations.SelectedItem = m;
            recall(m.Dial);
            SelectMemory(m);
        };
        save.Click += (_, _) =>
        {
            Suggest();
            string label = name.Text.Trim();
            var m = new StationMemory(Guid.NewGuid(), label.Length > 0 ? label : name.PlaceholderText, current());
            library.Memories.Add(m);
            Changed(m.Id);
        };
        rename.Click += (_, _) =>
        {
            if (Selected() is not { } m || string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            library.Memories[library.Memories.IndexOf(m)] = m with { Name = name.Text.Trim() };
            Changed(m.Id);
        };
        update.Click += (_, _) =>
        {
            if (Selected() is not { } m) return;
            library.Memories[library.Memories.IndexOf(m)] = m with { Dial = current() };
            Changed(m.Id);
        };
        remove.Click += (_, _) =>
        {
            if (Selected() is not { } m) return;
            library.Memories.Remove(m);
            Changed(null);
        };
        RefreshList(null);
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
