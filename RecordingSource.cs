using System.Text.Json;
using System.Runtime.InteropServices;

namespace Spectraxia;

// Index segment boundaries once. Only the selected 30 s window is read into RAM.
internal sealed class RecordingSource
{
    private sealed record Segment(CaptureMark Mark, long Start, long End, double Time);
    private readonly List<Segment> segments = [];
    private readonly string path;
    private readonly bool iq;
    private readonly int stride;
    private readonly long dataStart;
    internal double Duration { get; }
    internal int Pages => Math.Max(1, checked((int)Math.Ceiling((Duration - 1e-9) / 30)));

    internal static string? TimelineFor(string path)
    {
        string[] parts = Path.GetFileNameWithoutExtension(path).Split('-');
        if (parts.Length != 2 || parts[0] is not ("audio" or "wideband") || !int.TryParse(parts[1],out _)) return null;
        return Path.Combine(Path.GetDirectoryName(path)!, $"timeline-{parts[1]}.jsonl");
    }

    internal RecordingSource(string path, CaptureBand? rawFormat = null)
    {
        this.path = path;
        iq = Path.GetExtension(path).Equals(".cu8", StringComparison.OrdinalIgnoreCase);
        using var stream = File.OpenRead(path);
        long bytes = stream.Length;
        if (iq) stride = 2;
        else (dataStart, bytes, stride) = ReadWave(stream);
        if (bytes <= 0 || bytes % stride != 0) throw new InvalidDataException("Incomplete recording.");
        long units = bytes / stride;
        string? timeline = TimelineFor(path);
        var marks = File.Exists(timeline) ? File.ReadLines(timeline)
            .Select(s => JsonSerializer.Deserialize<CaptureMark>(s) ?? throw new InvalidDataException("Empty timeline entry.")).ToList() : [];
        if (marks.Count == 0)
        {
            if (File.Exists(timeline)) throw new InvalidDataException("The recording timeline is empty.");
            if (iq && (rawFormat==null || !Radio.SampleRates.Contains(rawFormat.Rate)))
                throw new InvalidDataException("Select the capture rate and center for this raw I/Q file.");
            var band = rawFormat ?? new(48000,0,0,ListenMode.WFM,200000,true,Guid.Empty);
            marks.Add(new(0,0,File.GetLastWriteTimeUtc(path).AddSeconds(-units/(double)band.Rate),band));
        }
        long Offset(CaptureMark m) => iq ? m.IqPairs : m.AudioFrames;
        if (Offset(marks[0]) != 0) throw new InvalidDataException("Timeline must start at sample zero.");
        double elapsed = 0;
        for (int i = 0; i < marks.Count; i++)
        {
            var m = marks[i];
            long start = Offset(m), end = i + 1 < marks.Count ? Offset(marks[i + 1]) : units;
            if (end < start || end > units || m.Band == null || (iq && !Radio.SampleRates.Contains(m.Band.Rate)) ||
                !Enum.IsDefined(m.Band.Mode) || m.Band.Center is < 0 or > TuningView.MaxHz ||
                !(new DialSetting(m.Band.Station, m.Band.Mode, m.Band.Width)).Valid)
                throw new InvalidDataException("Invalid recording timeline.");
            if (end == start) continue;
            segments.Add(new(m, start, end, elapsed));
            elapsed += (end - start) / (double)(iq ? m.Band.Rate : 48000);
        }
        Duration = elapsed;
    }
    private static (long Start, long Length, int Stride) ReadWave(FileStream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, true);
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Expected a PCM WAVE file.");
        r.ReadUInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("Expected WAVE.");
        int stride = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = new(r.ReadChars(4));
            long length = r.ReadUInt32(), start = stream.Position;
            if (length > stream.Length - start) throw new InvalidDataException("Truncated WAV chunk.");
            if (id == "fmt " && length >= 16)
            {
                int format = r.ReadUInt16(), channels = r.ReadUInt16(), rate = r.ReadInt32();
                r.ReadUInt32(); stride = r.ReadUInt16(); int bits = r.ReadUInt16();
                if (format != 1 || channels is < 1 or > 2 || rate != 48000 || bits != 16 || stride != channels * 2)
                    throw new InvalidDataException("Expected 48 kHz 16-bit mono or stereo PCM.");
            }
            if (id == "data" && stride != 0) return (start, length, stride);
            stream.Position = start + length + (length % 2);
        }
        throw new InvalidDataException("WAV data chunk missing.");
    }
    internal TapeBlock[] Page(int page, CancellationToken token = default)
    {
        if (page < 0 || page >= Pages) throw new ArgumentOutOfRangeException(nameof(page));
        double from = page * 30.0, to = from + 30;
        var result = new List<TapeBlock>();
        using var input = File.OpenRead(path);
        foreach (var s in segments)
        {
            if (s.Time >= to) break;
            int rate = iq ? s.Mark.Band.Rate : 48000;
            long first = s.Start + (long)Math.Round(Math.Max(0, from - s.Time) * rate);
            long last = Math.Min(s.End, s.Start + (long)Math.Round((to - s.Time) * rate));
            if (first >= last) continue;
            input.Position = dataStart + first * stride;
            for (long offset = first; offset < last;)
            {
                token.ThrowIfCancellationRequested();
                int n = (int)Math.Min(last - offset, iq ? 16384 : 960);
                byte[] raw = iq ? new byte[n * stride] : [];
                short[] audio = iq ? [] : new short[n * stride / 2];
                if (iq) input.ReadExactly(raw);
                else input.ReadExactly(MemoryMarshal.AsBytes(audio.AsSpan()));
                result.Add(new(s.Mark.Utc.AddSeconds((offset - s.Start) / (double)rate), s.Mark.Band, raw, audio, iq ? 0 : stride / 2));
                offset += n;
            }
        }
        return result.ToArray();
    }
}
