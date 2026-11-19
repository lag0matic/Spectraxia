using System.IO.Compression;

namespace Spectraxia;

internal sealed record AircraftReference(string Registration, string TypeCode, string Model)
{
    internal static AircraftReference? Lookup(string address)
    {
        using var resource = typeof(AircraftReference).Assembly.GetManifestResourceStream("Spectraxia.AircraftReference")!;
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        var entry = archive.GetEntry(address[..3]+".tsv");
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        string prefix = address+"\t";
        while (reader.ReadLine() is {} line)
        {
            if (!line.StartsWith(prefix,StringComparison.Ordinal)) continue;
            var fields = line.Split('\t');
            return new(fields[1],fields[2],fields[3]);
        }
        return null;
    }
}

internal sealed class AircraftDetails : Panel
{
    private readonly Label title = new() { AutoSize=true, UseMnemonic=false, Anchor=AnchorStyles.Left };
    private readonly Label reference = ContentLabel();
    private readonly Label flight = ContentLabel();
    private readonly Label reception = ContentLabel();
    private readonly Font headingFont;
    internal string? Address { get; private set; }

    internal AircraftDetails()
    {
        Visible=false; Dock=DockStyle.Right;
        BackColor=Color.FromArgb(32,44,54);
        ForeColor=Color.WhiteSmoke;
        AccessibleName="Aircraft details";
        var scroll=new Panel { Dock=DockStyle.Fill, AutoScroll=true };
        var body=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=1, Padding=new Padding(10,3,8,10) };
        body.ColumnStyles.Add(new(SizeType.Percent,100));
        headingFont=new Font(SystemFonts.MessageBoxFont!,FontStyle.Bold);
        void Section(string name, Label content)
        {
            body.Controls.Add(new Label { Text=name, AutoSize=true, Font=headingFont, ForeColor=Color.Aquamarine,
                Margin=new Padding(0,8,0,2) });
            body.Controls.Add(content);
        }
        Section("Mictronics aircraft reference · ODC-By 1.0",reference);
        Section("Live ADS-B",flight);
        Section("Reception",reception);
        var header=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=2, Padding=new Padding(8,3,4,2) };
        header.ColumnStyles.Add(new(SizeType.Percent,100)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        title.Font=headingFont;
        var close=new Button { Text="Close", AccessibleName="Close aircraft details", AutoSize=true,
            AutoSizeMode=AutoSizeMode.GrowAndShrink, FlatStyle=FlatStyle.Flat };
        close.Click+=(_,_)=> { Address=null; Visible=false; };
        header.Controls.Add(title); header.Controls.Add(close);
        scroll.Controls.Add(body); Controls.Add(scroll); Controls.Add(header);
        scroll.ClientSizeChanged+=(_,_)=>
        {
            int width=Math.Max(100,scroll.ClientSize.Width-body.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth);
            foreach (var label in new[]{reference,flight,reception}) label.MaximumSize=new(width,0);
        };
    }
    private static Label ContentLabel() => new() { AutoSize=true, UseMnemonic=false, Margin=Padding.Empty };

    internal void Open(AircraftRow row,long now)
    {
        if (Address != row.Address)
        {
            var match = AircraftReference.Lookup(row.Address);
            reference.Text = match==null ? "No match in the offline database." :
                $"Registration   {Value(match.Registration)}\nModel   {Value(match.Model)}\nICAO type   {Value(match.TypeCode)}";
            Address=row.Address;
        }
        Visible=true;
        UpdateContact(row,now);
    }
    internal void UpdateContact(AircraftRow? row,long now)
    {
        if (Address==null) return;
        if (row==null)
        {
            flight.Text="Contact expired";
            reception.Text="No messages for two minutes.";
            return;
        }
        title.Text=row.Callsign.Length==0 ? $"ICAO {row.Address}" : $"{row.Callsign} · {row.Address}";
        string position=row.Position is {} p ? $"{p.Lat:F5}°, {p.Lon:F5}°" : "—";
        string track=row.Track is {} t ? $"{(int)Math.Round(t)%360:000}° true" : "—";
        flight.Text=$"Category   {AircraftSymbol.Label(row.Category)}\nBarometric altitude   {Units(row.Altitude,"ft")}" +
            $"\nGround speed   {Units(row.Speed,"kt")}\nGround track   {track}\nVertical rate   {Units(row.Climb,"ft/min")}" +
            $"\nPosition   {position}";
        reception.Text=$"First heard   {Ago(now-row.FirstSeen)}\nLast heard   {Ago(now-row.Seen)}" +
            $"\nLast position   {(row.PositionSeen is {} located ? Ago(now-located) : "—")}\nValid messages   {row.Messages:N0}";
    }
    private static string Value(string text) => string.IsNullOrWhiteSpace(text) ? "—" : text;
    private static string Units(int? value,string unit) => value.HasValue ? $"{value:N0} {unit}" : "—";
    private static string Ago(long ms)
    {
        long seconds=Math.Max(0,ms/1000);
        return seconds < 60 ? $"{seconds}s ago" : $"{seconds/60}m {seconds%60}s ago";
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) headingFont.Dispose();
        base.Dispose(disposing);
    }
}
