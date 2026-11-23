using System.Collections.Concurrent;

namespace Spectraxia;

internal sealed class AprsInbox
{
    private readonly ConcurrentQueue<(DateTimeOffset Time,AprsPacket Packet)> pending = new();
    internal void Add(AprsPacket packet) => Add(DateTimeOffset.Now,packet);
    internal void Add(DateTimeOffset time,AprsPacket packet)
    {
        pending.Enqueue((time,packet));
        if (pending.Count>1000) pending.TryDequeue(out _);
    }
    internal bool Take(out (DateTimeOffset Time,AprsPacket Packet) value) => pending.TryDequeue(out value);
}

internal sealed class AprsMap : AircraftMap
{
    private readonly Dictionary<string,(GeoPosition Pos,bool Weather)> stations = new();
    internal void Remember(AprsPacket packet)
    {
        if (packet.Position is not {} fix) return;
        stations[packet.Source]=(fix,packet.Kind=="Weather");
        CenterFirst(fix);
        Invalidate();
    }
    internal void ClearStations() { stations.Clear(); Invalidate(); }
    internal void FocusStation(string call)
    {
        if (stations.TryGetValue(call,out var station)) CenterOn(station.Pos);
    }
    protected override void DrawContacts(Graphics g)
    {
        foreach (var (call,(fix,weather)) in stations)
        {
            var point=Screen(fix.Lon,fix.Lat);
            if (!ClientRectangle.Contains(Point.Round(point))) continue;
            float r=5*DeviceDpi/96f;
            g.FillEllipse(weather ? Brushes.LightSkyBlue : Brushes.MediumSpringGreen,point.X-r,point.Y-r,2*r,2*r);
            TextRenderer.DrawText(g,call,Font,new Point((int)(point.X+r+2),(int)(point.Y-r)),Color.WhiteSmoke,BackColor);
        }
        if (stations.Count==0) TextRenderer.DrawText(g,"Waiting for a station position",Font,new Point(8,8),Color.WhiteSmoke,BackColor);
    }
}

internal sealed class AprsWindow : Form
{
    internal static readonly DialSetting Dial = new(144390000,ListenMode.NFM,15000);
    private readonly System.Windows.Forms.Timer timer = new() { Interval=350 };
    private readonly CancellationTokenSource stop = new();
    internal AprsWindow(AprsInbox live,string rangePath,Func<string> radioStatus,Action tune)
    {
        Text="Spectraxia · APRS";
        Icon=Program.AppIcon;
        Size=new(940,670); MinimumSize=new(560,400);
        BackColor=Color.FromArgb(28,38,47); ForeColor=Color.WhiteSmoke;
        var map=new AprsMap { Dock=DockStyle.Fill, AccessibleName="APRS station map" };
        try { map.SetRange(MapRange.Load(rangePath)); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
        var log=new ListView { Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,HideSelection=false,
            BackColor=BackColor,ForeColor=ForeColor,MultiSelect=false };
        foreach(var (caption,width) in new[]{("Time",78),("Station",100),("Report",85),("Via",148),("Details",390)})
            log.Columns.Add(caption,width);
        var packetDetail = new TextBox { Dock=DockStyle.Bottom, ReadOnly=true, AccessibleName="Selected raw APRS packet",
            BackColor=BackColor, ForeColor=Color.WhiteSmoke, BorderStyle=BorderStyle.FixedSingle };
        log.SelectedIndexChanged += (_,_) => packetDetail.Text = log.SelectedItems.Count==1 ? log.SelectedItems[0].ToolTipText : "";
        var bar=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true };
        var tuneButton=new Button { Text="Tune 144.390",AutoSize=true,FlatStyle=FlatStyle.Flat };
        var replay=new Button { Text="Replay recording…",AutoSize=true,FlatStyle=FlatStyle.Flat };
        var status=new Label { AutoSize=true,Anchor=AnchorStyles.Left };
        bar.Controls.AddRange([tuneButton,replay,status]);
        var split=new SplitContainer { Dock=DockStyle.Fill,Orientation=Orientation.Horizontal };
        split.Panel1.Controls.Add(map); split.Panel2.Controls.Add(log); split.Panel2.Controls.Add(packetDetail);
        Controls.Add(split); Controls.Add(bar);
        Shown+=(_,_)=>split.SplitterDistance=split.Height*3/5;
        log.ItemActivate+=(_,_)=> { if(log.SelectedItems.Count==1) map.FocusStation(log.SelectedItems[0].SubItems[1].Text); };
        var inbox=live;
        int count=0;
        string? fileState=null;
        void Display()
        {
            log.BeginUpdate();
            try
            {
                while(inbox.Take(out var item))
                {
                    var p=item.Packet;
                    map.Remember(p);
                    string detail=p.Summary;
                    if(p.Position is {} pos) detail=$"{pos.Lat:F4}, {pos.Lon:F4}  {detail}";
                    string route = p.Path.Contains('*') ? p.Path : p.Path.Length == 0 ? "Direct" : "Direct · "+p.Path;
                    var row=new ListViewItem(new[]{item.Time.ToLocalTime().ToString("HH:mm:ss"),p.Source,p.Kind,route,detail});
                    row.ToolTipText=$"{p.Source}>{p.Destination}{(p.Path.Length>0 ? ","+p.Path : "")}:{p.Payload}";
                    log.Items.Insert(0,row);
                    if(log.Items.Count>500) log.Items.RemoveAt(log.Items.Count-1);
                    count++;
                }
            }
            finally {log.EndUpdate();}
            status.Text=$"{fileState ?? radioStatus()}  ·  {count} packets";
        }
        log.ShowItemToolTips=true;
        void Switch(AprsInbox next)
        {
            inbox=next; log.Items.Clear(); count=0;
            map.ClearStations();
        }
        tuneButton.Click+=(_,_)=> { tune(); if(inbox!=live) Switch(live); fileState=null; Display(); };
        replay.Click+=async (_,_)=>
        {
            using var dialog=new OpenFileDialog { Filter="Spectraxia recording (*.cu8;*.wav)|*.cu8;*.wav" };
            if(dialog.ShowDialog(this)!=DialogResult.OK) return;
            string path = dialog.FileName;
            CaptureBand? format = null;
            if (Path.GetExtension(path).Equals(".cu8",StringComparison.OrdinalIgnoreCase) && !File.Exists(RecordingSource.TimelineFor(path)))
            {
                format = AskCaptureFormat();
                if (format == null) return;
            }
            var token = stop.Token;
            var recorded=new AprsInbox();
            Switch(recorded); replay.Enabled=tuneButton.Enabled=false;
            var filename=Path.GetFileName(dialog.FileName);
            fileState="Reading "+filename; Display();
            try
            {
                await Task.Run(()=>AprsReplay.Read(path,format,recorded.Add,token));
                fileState="Replay · "+filename;
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) {return;}
            catch(Exception ex) when(ex is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {fileState=ex.Message;}
            finally {if(!IsDisposed && !Disposing) {replay.Enabled=tuneButton.Enabled=true; Display();}}
        };
        timer.Tick+=(_,_)=>Display();
        FormClosed+=(_,_)=>stop.Cancel();
        timer.Start(); Display();
    }
    private CaptureBand? AskCaptureFormat()
    {
        using var prompt = new Form { Text = "Raw I/Q recording", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, BackColor = BackColor, ForeColor = ForeColor };
        var fields = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8) };
        var rate = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Sample rate" };
        rate.Items.AddRange(Radio.SampleRates.Select(n=>$"{n/1e6:0.00} MS/s").ToArray());
        rate.SelectedIndex = Array.IndexOf(Radio.SampleRates,Radio.CaptureRate);
        var center = new NumericUpDown { DecimalPlaces = 6, Maximum = 1766, Value = 144.390m, Increment = .001m,
            AccessibleName = "Capture center in MHz" };
        var read = new Button { Text = "Read capture", AutoSize = true, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat };
        fields.Controls.AddRange([new Label { Text="Capture sample rate",AutoSize=true },rate,
            new Label { Text="Capture center (MHz)",AutoSize=true },center,read]);
        prompt.Controls.Add(fields);
        prompt.AcceptButton=read;
        if(prompt.ShowDialog(this)!=DialogResult.OK) return null;
        return new(Radio.SampleRates[rate.SelectedIndex],(long)(center.Value*1000000),Dial.Hz,ListenMode.NFM,Dial.Width,true,Guid.Empty);
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing && !IsDisposed) {stop.Cancel(); timer.Dispose(); stop.Dispose();}
        base.Dispose(disposing);
    }
}
