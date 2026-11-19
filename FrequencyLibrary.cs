using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spectraxia;

internal sealed record DialSetting(long Hz, ListenMode Mode, int Width, int? HdProgram = null)
{
    internal bool Valid => Hz >= 0 && Hz <= TuningView.MaxHz && Enum.IsDefined(Mode)
        && Width >= Listening.Range(Mode).Min && Width <= Listening.Range(Mode).Max
        && (Mode != ListenMode.WFM || Width % 5000 == 0)
        && (HdProgram == null || (Mode == ListenMode.WFM && HdReceiver.InBand(Hz) && HdProgram is >= 0 and < 8));

    internal ReceiverSettings Apply(ReceiverSettings receiver) => Mode == ListenMode.WFM
        ? receiver with { Mode = Mode, WfmBandwidthKhz = Width / 1000 }
        : receiver with { Mode = Mode, FilterHz = Width };
}

internal sealed record StationMemory(Guid Id, string Name, DialSetting Dial)
{
    public override string ToString() => $"{Name}  ·  {Dial.Hz / 1e6:0.######} MHz" +
        (Dial.HdProgram is int program ? $"  HD{program + 1}" : "");
}

internal sealed class FrequencyLibrary
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<ListenMode>() }
    };
    public Dictionary<string, DialSetting> Bands { get; init; } = [];
    public List<StationMemory> Memories { get; init; } = [];

    internal void Remember(DialSetting dial)
    {
        if (BandPlan.At(dial.Hz) is { } band) Bands[band] = dial with { HdProgram = null };
    }

    internal DialSetting Recall(string band) => Bands.TryGetValue(band, out var saved)
        ? saved : BandPlan.Start(band);

    internal static FrequencyLibrary Load(string path)
    {
        if (!File.Exists(path)) return new();
        var library = JsonSerializer.Deserialize<FrequencyLibrary>(File.ReadAllText(path), Format);
        if (library?.Bands == null || library.Memories == null ||
            library.Bands.Any(p => p.Value is not { Valid: true } || BandPlan.At(p.Value.Hz) != p.Key) ||
            library.Memories.Any(m => m == null || string.IsNullOrWhiteSpace(m.Name) || m.Dial is not { Valid: true }) ||
            library.Memories.Select(m => m.Id).Distinct().Count() != library.Memories.Count)
            throw new InvalidDataException("Invalid station library.");
        return library;
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Format));
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
