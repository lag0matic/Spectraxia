using System.Text.Json;

namespace Spectraxia;

internal sealed record ReceiverSettings(bool Manual = false, int GainTenths = 0, bool DigitalAgc = false,
    bool SquelchEnabled = false, int SquelchDbfs = -55, int WfmBandwidthKhz = 200, int SampleRate = 2_400_000,
    bool AverageTrace = false, bool PeakHold = false, int HoldSeconds = 3, int WaterfallMin = -85, int WaterfallMax = -22, ListenMode Mode = ListenMode.WFM, int FilterHz = 0, bool ForceMono = false,
    Dictionary<string, AgcSpeed>? AudioAgcByMode = null, bool RemoveDc = false,
    ManualNotch[]? Notches = null, bool BypassNotches = false, int AutoNotchHz = 0,
    bool BalanceIq = false, decimal QGainDb = 0, decimal QPhaseDegrees = 0, string? RecordingFolder = null)
{
    internal int Width { get { var r = Listening.Range(Mode); return Mode == ListenMode.WFM ? WfmBandwidthKhz * 1000 : Math.Clamp(FilterHz == 0 ? r.Default : FilterHz, r.Min, r.Max); } }
    internal static int BandwidthStep(int khz) => Math.Clamp((int)Math.Round(Math.Clamp(khz, 50, 200) / 5.0) * 5, 50, 200);
    internal int NearestGain(int[] steps) => steps.MinBy(step => Math.Abs((long)step - GainTenths));

    internal AgcSpeed AudioSpeed => Mode != ListenMode.IQ && AudioAgcByMode != null &&
        AudioAgcByMode.TryGetValue(Mode.ToString(), out var value) && Enum.IsDefined(value) ? value : AgcSpeed.Off;

    internal ReceiverSettings WithAudioSpeed(AgcSpeed speed)
    {
        var speeds = AudioAgcByMode == null ? new Dictionary<string, AgcSpeed>() : new(AudioAgcByMode);
        speeds[Mode.ToString()] = speed;
        return this with { AudioAgcByMode = speeds };
    }

    internal bool PassSignal(double dbfs, bool wasOpen) =>
        !SquelchEnabled || dbfs >= SquelchDbfs - (wasOpen ? 2 : 0);

    internal static ReceiverSettings Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var value = root.Deserialize<ReceiverSettings>();
            if (value != null && !root.TryGetProperty(nameof(SquelchEnabled), out _) &&
                root.TryGetProperty("WfmSquelch", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                value = value with { SquelchEnabled = enabled.GetBoolean() };
            if (value != null && !root.TryGetProperty(nameof(SquelchDbfs), out _) &&
                root.TryGetProperty("WfmThreshold", out var threshold) && threshold.ValueKind == JsonValueKind.Number && threshold.TryGetInt32(out int legacy))
                value = value with { SquelchDbfs = legacy };
            if (value != null) return value with { SquelchDbfs = Math.Clamp(value.SquelchDbfs, -95, 0), HoldSeconds = Math.Clamp(value.HoldSeconds, 0, 30),
                WfmBandwidthKhz = BandwidthStep(value.WfmBandwidthKhz),
                AutoNotchHz = value.AutoNotchHz is 40 or 100 or 220 ? value.AutoNotchHz : 0,
                QGainDb = Math.Clamp(value.QGainDb, -6m, 6m), QPhaseDegrees = Math.Clamp(value.QPhaseDegrees, -20m, 20m),
                Notches = value.Notches?.Where(n => n != null && n.Hz is >= 0 and <= TuningView.MaxHz &&
                    ManualNotch.Widths.Contains(n.WidthHz)).DistinctBy(n => n.Hz).ToArray(),
                Mode = Enum.IsDefined(value.Mode) ? value.Mode : ListenMode.WFM,
                WaterfallMin = Math.Clamp(value.WaterfallMin, -95, -1),
                WaterfallMax = Math.Clamp(value.WaterfallMax, Math.Clamp(value.WaterfallMin, -95, -1) + 1, 0),
                SampleRate = Radio.SampleRates.Contains(value.SampleRate) ? value.SampleRate : 2_400_000 };
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return new();
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
    }
}
