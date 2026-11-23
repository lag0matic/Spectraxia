namespace Spectraxia;

internal sealed class AdsbWindow : Form
{
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 700 };
    private readonly AircraftMap map = new() { Dock = DockStyle.Fill };
    private readonly AircraftDetails details = new();
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        MultiSelect = true, HideSelection = false, BackColor = Color.FromArgb(24,33,41), ForeColor = Color.WhiteSmoke };
    private readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly AdsbTraffic traffic;
    internal AdsbWindow(AdsbTraffic traffic, AdsbTuner tuner, Func<BiasState> biasState, Action toggleBias)
        : this(traffic, tuner, null, biasState, toggleBias) { }
    internal AdsbWindow(AdsbTraffic traffic, HackrfTuner tuner) : this(traffic, null, tuner, null, null) { }
    private AdsbWindow(AdsbTraffic traffic, AdsbTuner? tuner, HackrfTuner? hackrf, Func<BiasState>? biasState, Action? toggleBias)
    {
        Icon = Program.AppIcon;
        this.traffic = traffic;
        Text = hackrf == null ? "Spectraxia · RTL-SDR ADS-B aircraft" : "Spectraxia · HackRF ADS-B aircraft";
        StartPosition = FormStartPosition.CenterParent;
        Size = new(1080,720); MinimumSize = new(650,450);
        BackColor = Color.FromArgb(28,38,47); ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI",10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent,68));
        layout.RowStyles.Add(new(SizeType.Percent,32));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var back = new Button { Text = hackrf == null ? "Return to radio" : "Close aircraft", AutoSize = true, FlatStyle = FlatStyle.Flat };
        back.Click += (_,_) => Close();
        var bias = new Button { AutoSize = true, FlatStyle = FlatStyle.Flat, Visible = biasState != null };
        bias.Click += (_,_) => { toggleBias?.Invoke(); bias.Enabled = false; };
        var settings = new CheckBox { Text = "Receiver settings", Appearance = Appearance.Button, AutoSize = true };
        var rtlGain = tuner == null ? null : new AdsbGainPanel(tuner);
        var hackGain = hackrf == null ? null : new HackrfGainPanel(hackrf);
        Control gain = (Control?)rtlGain ?? hackGain!;
        gain.Visible = false;
        settings.CheckedChanged += (_,_) => gain.Visible = settings.Checked;
        var range = new Button { Text = "Range ▾", AutoSize = true, FlatStyle = FlatStyle.Flat };
        var rangeMenu = new ContextMenuStrip();
        var place = rangeMenu.Items.Add("Place receiver on map");
        var rings = new ToolStripMenuItem("Show range rings") { CheckOnClick = true };
        rangeMenu.Items.Add(rings);
        var hint = new Label { AutoSize = true };
        const string gestures = "Click a plane for its trail · Ctrl-click to select more · Double-click a list row for details · Drag to pan · Wheel to zoom";
        hint.Text = gestures;
        string rangeFile = Path.ChangeExtension(AppPaths.Settings,".map-range.json");
        try { map.SetRange(MapRange.Load(rangeFile)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { hint.Text = "Could not read the saved receiver position. " + ex.Message; }
        void SaveRange(MapRange value)
        {
            map.SetRange(value);
            try { value.Save(rangeFile); hint.Text = gestures; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { hint.Text = "Receiver position applies this session. " + ex.Message; }
        }
        void PlaceReceiver(bool active)
        {
            map.PlacingReceiver = active;
            map.Cursor = active ? Cursors.Cross : Cursors.Default;
            range.Text = active ? "Cancel placement" : "Range ▾";
            hint.Text = active ? "Click your antenna location on the map. You can pan and zoom first." : gestures;
        }
        range.Click += (_,_) =>
        {
            if (map.PlacingReceiver) { PlaceReceiver(false); return; }
            rings.Checked = map.Range.ShowRings;
            rings.Enabled = map.Range.Site.HasValue;
            rangeMenu.Show(range,new Point(0,range.Height));
        };
        place.Click += (_,_) => PlaceReceiver(true);
        rings.Click += (_,_) => SaveRange(map.Range with { ShowRings = rings.Checked });
        map.ReceiverPlaced += fix =>
        {
            PlaceReceiver(false);
            SaveRange(map.Range with { Site = fix, ShowRings = true });
        };
        Disposed += (_,_) => rangeMenu.Dispose();
        bar.Controls.AddRange([back,bias,settings,range,status]);
        var mapArea=new Panel { Dock=DockStyle.Fill, Margin=map.Margin };
        mapArea.Controls.Add(map); mapArea.Controls.Add(details);
        mapArea.ClientSizeChanged+=(_,_)=>details.Width=Math.Min(mapArea.ClientSize.Width/2,LogicalToDeviceUnits(335));
        layout.Controls.Add(bar,0,0); layout.Controls.Add(gain,0,1);
        layout.Controls.Add(mapArea,0,2); layout.Controls.Add(list,0,3);
        foreach (var (name,width) in new[] { ("ICAO",78),("Callsign",95),("Alt (ft)",86),("GS (kt)",78),
            ("Track (°)",84),("V/S (ft/min)",112),("Category",116),("Range (NM)",92),("Position",177),("Seen",72),("Msgs",75) }) list.Columns.Add(name,width);
        list.SelectedIndexChanged += (_,_) =>
            map.SelectAircraft(list.SelectedItems.Cast<ListViewItem>().Select(item => item.Name));
        map.AircraftClicked += (address, additive) =>
        {
            if (list.Items[address] is not {} item) return;
            bool select = !additive || !item.Selected;
            if (!additive) list.SelectedItems.Clear();
            item.Selected = select;
            if (!select) return;
            item.Focused = true;
            item.EnsureVisible();
            if (details.Address != null && item.Tag is AircraftRow row) details.Open(row,Environment.TickCount64);
        };
        list.ItemActivate += (_,_) =>
        {
            if (list.SelectedItems.Count!=1 || list.SelectedItems[0].Tag is not AircraftRow row) return;
            details.Open(row,Environment.TickCount64);
            if (row.Position is {} fix) map.CenterOn(fix);
        };
        layout.Controls.Add(hint,0,4);
        Controls.Add(layout);
        void RefreshTraffic()
        {
            if (biasState != null) BiasButton.Update(bias, biasState());
            long now = Environment.TickCount64;
            tuner?.Flush();
            hackrf?.Flush();
            int recent = traffic.RecentMessages(now);
            rtlGain?.RefreshReceiver(traffic.GainSteps, recent);
            hackGain?.RefreshReceiver(recent, hackrf!.FileStatus);
            var rows = traffic.Snapshot(now);
            status.Text = $"{traffic.Status}   ·   {traffic.Messages:N0} messages   ·   {rows.Length} aircraft";
            list.BeginUpdate();
            try
            {
                var current = rows.Select(row=>row.Address).ToHashSet();
                for (int i=list.Items.Count-1; i>=0; i--)
                    if (!current.Contains(list.Items[i].Name)) list.Items.RemoveAt(i);
                foreach (var row in rows)
                {
                    string[] cells = [row.Address,row.Callsign,row.Altitude?.ToString("N0")??"",
                        row.Speed?.ToString()??"",row.Track?.ToString("F0")??"",row.Climb?.ToString()??"",
                        AircraftSymbol.Label(row.Category),
                        row.Position is {} position && map.Range.Site is {} site ? MapRange.Distance(site,position).ToString("F1") : "",
                        row.Position is {} fix ? $"{fix.Lat:F4}, {fix.Lon:F4}" : "",
                        $"{(now-row.Seen)/1000}s",row.Messages.ToString()];
                    var item = list.Items[row.Address];
                    if (item != null)
                    {
                        item.Tag = row;
                        for (int i=1; i<cells.Length; i++)
                            if (item.SubItems[i].Text != cells[i]) item.SubItems[i].Text = cells[i];
                    }
                    else item = list.Items.Add(new ListViewItem(cells) { Name=row.Address, Tag=row, UseItemStyleForSubItems=false });
                    item.SubItems[6].ForeColor = AircraftSymbol.Tint(row.Category);
                }
            }
            finally { list.EndUpdate(); }
            if (details.Address is {} address) details.UpdateContact(list.Items[address]?.Tag as AircraftRow,now);
            map.SetAircraft(rows,now);
        }
        Shown += (_,_) => { RefreshTraffic(); refresh.Start(); };
        FormClosed += (_,_) => { tuner?.Flush(); hackrf?.Flush(); };
        refresh.Tick += (_,_) => RefreshTraffic();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) refresh.Dispose();
        base.Dispose(disposing);
    }
}
