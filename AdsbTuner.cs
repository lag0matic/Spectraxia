using System.Text.Json;

namespace Spectraxia;

internal sealed record AdsbGain(bool Manual, int GainTenths, bool DigitalAgc);

// The UI owns the profile. The USB control loop reads one immutable capture snapshot.
internal sealed class AdsbTuner
{
    private readonly string path;
    private volatile ReceiverSettings capture;
    private bool dirty;
    internal ReceiverSettings Capture => capture;
    internal string FileStatus { get; private set; } = "";

    internal AdsbTuner(string path, ReceiverSettings listening)
    {
        this.path = path;
        var gain = new AdsbGain(listening.Manual, listening.GainTenths, listening.DigitalAgc);
        try
        {
            gain = JsonSerializer.Deserialize<AdsbGain>(File.ReadAllText(path))
                ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { dirty = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { FileStatus = "Could not read saved ADS-B gain."; }
        capture = new(Manual: gain.Manual, GainTenths: gain.GainTenths, DigitalAgc: gain.DigitalAgc, SampleRate: AdsbDecoder.Rate);
    }

    internal void Set(bool manual, int gainTenths, bool digitalAgc)
    {
        var next = capture with { Manual = manual, GainTenths = gainTenths, DigitalAgc = digitalAgc };
        if (next == capture) return;
        capture = next;
        dirty = true;
    }

    internal void Flush()
    {
        if (!dirty) return;
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(new AdsbGain(capture.Manual, capture.GainTenths, capture.DigitalAgc)));
            File.Move(temp, path, true);
            dirty = false;
            FileStatus = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { FileStatus = "ADS-B gain applies this session. Could not save."; }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class AdsbGainPanel : FlowLayoutPanel
{
    private readonly AdsbTuner tuner;
    private readonly CheckBox manual = new() { Text = "Manual tuner gain", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TrackBar slider = new() { Width = 160, Height = 28, AutoSize = false,
        SmallChange = 1, LargeChange = 1, AccessibleName = "ADS-B manual tuner gain", TickStyle = TickStyle.None };
    private readonly Label level = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox agc = new() { Text = "RTL digital AGC", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label rate = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label fileStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.Khaki };
    private int[]? steps;
    private bool syncing;

    internal AdsbGainPanel(AdsbTuner tuner)
    {
        this.tuner = tuner;
        Dock = DockStyle.Fill;
        AutoSize = true;
        manual.Checked = tuner.Capture.Manual;
        agc.Checked = tuner.Capture.DigitalAgc;
        Controls.AddRange([manual, slider, level, agc, rate, fileStatus]);
        manual.CheckedChanged += (_, _) => { tuner.Set(manual.Checked, tuner.Capture.GainTenths, agc.Checked); ShowGain(); };
        agc.CheckedChanged += (_, _) => tuner.Set(manual.Checked, tuner.Capture.GainTenths, agc.Checked);
        slider.ValueChanged += (_, _) =>
        {
            if (syncing || !manual.Checked || steps is not { Length: > 0 }) return;
            tuner.Set(true, steps[slider.Value], agc.Checked);
            ShowGain();
        };
        ShowGain();
    }
    private void ShowGain()
    {
        manual.Enabled = steps is { Length: > 0 };
        agc.Enabled = steps != null;
        slider.Enabled = manual.Enabled && manual.Checked;
        level.Text = steps == null ? "Waiting for receiver" : !manual.Enabled ? "Auto gain only" :
            manual.Checked ? $"{steps[slider.Value] / 10m:0.0} dB" : "Auto gain";
    }
    internal void RefreshReceiver(int[]? available, int messages)
    {
        if (available != steps)
        {
            steps = available;
            syncing = true;
            if (steps is { Length: > 0 })
            {
                slider.Maximum = steps.Length - 1;
                slider.Value = Array.IndexOf(steps, tuner.Capture.NearestGain(steps));
            }
            syncing = false;
            ShowGain();
        }
        rate.Text = $"{messages:N0} valid messages / 10 s";
        fileStatus.Text = tuner.FileStatus;
        fileStatus.Visible = fileStatus.Text.Length > 0;
    }
}
