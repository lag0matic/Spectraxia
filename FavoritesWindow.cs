namespace Spectraxia;

internal sealed class FavoritesWindow : Form
{
    private readonly FrequencyLibrary library;
    private readonly Action<DialSetting> recall;
    private readonly Action changed;
    private readonly FlowLayoutPanel groups = Bar(), cards = Bar();
    private readonly TextBox name = new() { Width = 175, MaxLength = 80, AccessibleName = "Station name" };
    private readonly TextBox category = new() { Width = 135, MaxLength = 48, PlaceholderText = "Auto (band)", AccessibleName = "Category" };
    private readonly Button sort = ActionButton("Sort by name"), scan = ActionButton("Scan checked");
    private readonly Button edit = ActionButton("Save labels"), update = ActionButton("Update tuning"), remove = ActionButton("Remove");
    private readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly bool writable;
    private string? group;
    private Guid? selection;
    private bool byName;
    private StationMemory? Selected => library.Memories.FirstOrDefault(m => m.Id == selection);

    internal FavoritesWindow(FrequencyLibrary library, Func<DialSetting> current, Func<string> decoded,
        Action<DialSetting> recall, Action changed, Func<StationMemory[],bool> start, bool writable)
    {
        this.library = library;
        this.recall = recall;
        this.changed = changed;
        this.writable = writable;
        byName = library.SortFavoritesByName;
        Icon = Program.AppIcon;
        Text = "Spectraxia · Favorites";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new(475,380);
        Size = new(760,590);
        BackColor = Color.FromArgb(28,38,47);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(6) };
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        foreach (var style in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize })
            layout.RowStyles.Add(new(style,style == SizeType.Percent ? 100 : 0));
        var bar = Bar();
        bar.Controls.AddRange([sort,scan,status]);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        cards.Dock = DockStyle.Top;
        scroll.Controls.Add(cards);
        var editor = Bar();
        var add = ActionButton("Save current as new");
        editor.Controls.AddRange([Caption("Name"),name,Caption("Category"),category,add,edit,update,remove]);
        editor.Enabled = writable;
        layout.Controls.Add(groups,0,0);
        layout.Controls.Add(bar,0,1);
        layout.Controls.Add(scroll,0,2);
        layout.Controls.Add(editor,0,3);
        Controls.Add(layout);
        sort.Click += (_,_) =>
        {
            byName = !byName;
            if (writable) { library.SortFavoritesByName = byName; changed(); }
            RefreshButtons();
        };
        scan.Click += (_,_) =>
        {
            var chosen = VisibleStations().Where(m => m.IncludeInScan).ToArray();
            if (chosen.Length > 0 && start(chosen)) Hide();
            else status.Text = "Start the listening receiver to scan.";
        };
        add.Click += (_,_) =>
        {
            var dial = current();
            var label = name.Text.Trim();
            if (label.Length == 0) label = decoded().Trim();
            if (label.Length == 0) label = $"{dial.Hz/1e6:0.######} MHz";
            var memory = new StationMemory(Guid.NewGuid(),label,dial,category.Text.Trim());
            library.Memories.Add(memory);
            AfterEdit(memory);
        };
        edit.Click += (_,_) =>
        {
            if (Selected is not {} old || string.IsNullOrWhiteSpace(name.Text)) return;
            var memory = old with { Name = name.Text.Trim(), Category = category.Text.Trim() };
            library.Memories[library.Memories.IndexOf(old)] = memory;
            AfterEdit(memory);
        };
        update.Click += (_,_) =>
        {
            if (Selected is not {} old) return;
            var memory = old with { Dial = current() };
            library.Memories[library.Memories.IndexOf(old)] = memory;
            AfterEdit(memory);
        };
        remove.Click += (_,_) =>
        {
            if (Selected is not {} old) return;
            library.Memories.Remove(old);
            AfterEdit(null);
        };
        RefreshButtons();
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
    private static FlowLayoutPanel Bar() => new() { Dock = DockStyle.Fill, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
    private static Button ActionButton(string text) => new() { Text = text, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat };
    private IEnumerable<StationMemory> VisibleStations()
    {
        var matching = library.Memories.Where(m => group == null || StringComparer.OrdinalIgnoreCase.Equals(group,m.Group));
        return byName ? matching.OrderBy(m => m.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Dial.Hz)
            : matching.OrderBy(m => m.Dial.Hz).ThenBy(m => m.Name,StringComparer.CurrentCultureIgnoreCase);
    }
    private void AfterEdit(StationMemory? memory)
    {
        changed();
        selection = memory?.Id;
        if (group != null) group = memory?.Group;
        RefreshButtons();
    }
    internal void SelectStation(Guid id)
    {
        if (IsDisposed) return;
        selection = id;
        if (group != null && !StringComparer.OrdinalIgnoreCase.Equals(group,Selected?.Group))
        {
            group = Selected?.Group;
            RefreshButtons();
            return;
        }
        foreach (Control card in cards.Controls)
            foreach (Button button in card.Controls.OfType<Button>())
                button.BackColor = Equals(button.Tag,id) ? Color.FromArgb(47,86,109) : BackColor;
        RefreshEditor();
    }
    private static void ClearButtons(Control panel)
    {
        while (panel.Controls.Count > 0) panel.Controls[0].Dispose();
    }
    private void RefreshButtons()
    {
        groups.SuspendLayout();
        cards.SuspendLayout();
        ClearButtons(groups);
        ClearButtons(cards);
        void AddGroup(string? key)
        {
            var button = ActionButton(key ?? "All favorites");
            if (StringComparer.OrdinalIgnoreCase.Equals(key,group)) button.BackColor = Color.FromArgb(47,86,109);
            button.Click += (_,_) => { group = key; selection = null; RefreshButtons(); };
            groups.Controls.Add(button);
        }
        AddGroup(null);
        foreach (var key in library.Memories.Select(m => m.Group).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
            AddGroup(key);
        var visible = VisibleStations().ToArray();
        foreach (var memory in visible)
        {
            var card = Bar();
            card.Dock = DockStyle.None;
            card.WrapContents = false;
            card.Margin = new Padding(3);
            var include = new CheckBox { AutoSize = true, Checked = memory.IncludeInScan, Anchor = AnchorStyles.Left,
                Enabled = writable, AccessibleName = $"Scan {memory.Name}" };
            var tune = ActionButton(memory.Name + Environment.NewLine + $"{memory.Dial.Hz/1e6:0.######} MHz · {Listening.Name(memory.Dial.Mode)}" +
                (memory.Dial.HdProgram is int p ? $" HD{p+1}" : ""));
            tune.TextAlign = ContentAlignment.MiddleLeft;
            tune.Tag = memory.Id;
            if (selection == memory.Id) tune.BackColor = Color.FromArgb(47,86,109);
            tune.Click += (_,_) => { recall(memory.Dial); SelectStation(memory.Id); };
            include.CheckedChanged += (_,_) =>
            {
                int index = library.Memories.FindIndex(m => m.Id == memory.Id);
                library.Memories[index] = library.Memories[index] with { IncludeInScan = include.Checked };
                changed();
                ShowScanCount();
            };
            card.Controls.AddRange([include,tune]);
            cards.Controls.Add(card);
        }
        sort.Text = byName ? "Sort by frequency" : "Sort by name";
        RefreshEditor();
        ShowScanCount();
        cards.ResumeLayout(true);
        groups.ResumeLayout(true);
    }
    private void RefreshEditor()
    {
        name.Text = Selected?.Name ?? "";
        category.Text = Selected?.Category ?? "";
        update.Enabled = edit.Enabled = remove.Enabled = Selected != null;
    }
    private void ShowScanCount()
    {
        int count = VisibleStations().Count(m => m.IncludeInScan);
        scan.Text = $"Scan checked ({count})";
        scan.Enabled = count > 0;
        status.Text = writable ? "" : "Library is read-only";
    }
}
