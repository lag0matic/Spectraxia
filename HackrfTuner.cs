using System.Text.Json;

namespace Spectraxia;

internal sealed record HackrfGain(int Lna = 16, int Vga = 16)
{
    internal bool Valid => Lna is >= 0 and <= 40 && Lna % 8 == 0 && Vga is >= 0 and <= 62 && Vga % 2 == 0;
}

internal sealed class HackrfTuner
{
    private readonly string path;
    private volatile HackrfGain gain = new();
    private bool dirty;
    internal HackrfGain Gain => gain;
    internal string FileStatus { get; private set; } = "";
    internal HackrfTuner(string path)
    {
        this.path = path;
        try
        {
            if (File.Exists(path))
                gain = JsonSerializer.Deserialize<HackrfGain>(File.ReadAllText(path)) is { Valid: true } saved
                    ? saved : throw new JsonException();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { FileStatus = "Could not read HackRF gain settings."; }
    }
    internal void Set(HackrfGain next)
    {
        if (!next.Valid || next == gain) return;
        gain = next;
        dirty = true;
    }
    internal void Flush()
    {
        if (!dirty) return;
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(gain));
            File.Move(temp, path, true);
            dirty = false;
            FileStatus = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { FileStatus = "Could not save HackRF gain settings."; }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class HackrfGainPanel : FlowLayoutPanel
{
    private readonly Label rate = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    internal HackrfGainPanel(HackrfTuner tuner)
    {
        AutoSize = true; Dock = DockStyle.Fill;
        void AddGain(string name, int step, int max, int initial, Action<int> change)
        {
            var label = new Label { AutoSize = true, Text = $"{name} {initial} dB", Anchor = AnchorStyles.Left };
            var slider = new TrackBar { AccessibleName = $"HackRF {name} gain", Width = 135, Height = 28,
                AutoSize = false, TickStyle = TickStyle.None, Maximum = max/step, SmallChange = 1, LargeChange = 1, Value = initial/step };
            slider.ValueChanged += (_,_) => { int db = slider.Value*step; change(db); label.Text = $"{name} {db} dB"; };
            Controls.AddRange([label,slider]);
        }
        AddGain("LNA",8,40,tuner.Gain.Lna, db => tuner.Set(tuner.Gain with { Lna = db }));
        AddGain("VGA",2,62,tuner.Gain.Vga, db => tuner.Set(tuner.Gain with { Vga = db }));
        Controls.Add(rate);
    }
    internal void RefreshReceiver(int recent, string fileStatus) =>
        rate.Text = $"{recent:N0} valid messages / 10 s  {fileStatus}".TrimEnd();
}
