using System.Threading.Channels;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Spectraxia;

internal sealed record CaptureBand(int Rate, long Center, long Station, ListenMode Mode, int Width, bool Settled, Guid Session);
internal sealed record TapeBlock(DateTimeOffset Utc, CaptureBand Band, byte[] Iq, short[] Pcm, int Channels)
{
    internal double Seconds => Iq.Length > 0 ? Iq.Length / (2.0 * Band.Rate) : Pcm.Length / (48000.0 * Channels);
}
internal sealed record CaptureMark(long IqPairs, long AudioFrames, DateTimeOffset Utc, CaptureBand Band);
internal enum CaptureTracks { Audio, Wideband, Both }

internal sealed class CaptureTape
{
    private readonly object sync = new();
    private readonly Queue<TapeBlock> ring = new();
    private double seconds;
    private Channel<TapeBlock>? output;
    internal TapeBlock[] Pin() { lock (sync) return ring.ToArray(); }
    internal void Append(TapeBlock block)
    {
        lock (sync)
        {
            ring.Enqueue(block);
            seconds += block.Seconds;
            while (ring.Count > 1 && (seconds - ring.Peek().Seconds >= 30 ||
                (block.Utc - ring.Peek().Utc).TotalSeconds > 30))
                seconds -= ring.Dequeue().Seconds;
            if (output != null && !output.Writer.TryWrite(block))
            {
                output.Writer.TryComplete(new IOException("Recording stopped because the disk writer fell behind."));
                output = null;
            }
        }
    }

    internal Task<string> Start(string folder, CaptureTracks tracks, bool leadIn)
    {
        lock (sync)
        {
            if (output != null) throw new InvalidOperationException("Recording already started.");
            var queue = Channel.CreateBounded<TapeBlock>(new BoundedChannelOptions(256) { SingleReader = true });
            var prefix = leadIn ? ring.ToArray() : [];
            output = queue;
            return Task.Run(async () =>
            {
                try
                {
                    using var files = new CaptureFiles(folder, tracks);
                    foreach (var b in prefix) files.Write(b);
                    await foreach (var b in queue.Reader.ReadAllAsync()) files.Write(b);
                    return files.Finish();
                }
                finally
                {
                    lock (sync) { if (output == queue) output = null; }
                    queue.Writer.TryComplete();
                }
            });
        }
    }
    internal void Stop()
    {
        lock (sync) { output?.Writer.TryComplete(); output = null; }
    }
    internal static string Export(string folder, CaptureTracks tracks, TapeBlock[] blocks)
    {
        using var files = new CaptureFiles(folder, tracks);
        foreach (var block in blocks) files.Write(block);
        return files.Finish();
    }
}

internal sealed class CaptureFiles : IDisposable
{
    private readonly string destination, working;
    private readonly CaptureTracks tracks;
    private StreamWriter? timeline;
    private FileStream? iq;
    private BinaryWriter? wav;
    private CaptureBand? previous;
    private long pairs, frames;
    private int part;
    private short[] stereo = [];
    internal CaptureFiles(string folder, CaptureTracks tracks)
    {
        this.tracks = tracks;
        destination = Path.Combine(folder, $"Spectraxia-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        working = destination + ".partial";
    }
    private void OpenPart()
    {
        Directory.CreateDirectory(working);
        part++;
        string PathFor(string name, string ext) => Path.Combine(working, $"{name}-{part:000}.{ext}");
        timeline = new StreamWriter(new FileStream(PathFor("timeline", "jsonl"), FileMode.CreateNew));
        if (tracks != CaptureTracks.Audio) iq = new FileStream(PathFor("wideband", "cu8"), FileMode.CreateNew);
        if (tracks != CaptureTracks.Wideband)
        {
            wav = new BinaryWriter(new FileStream(PathFor("audio", "wav"), FileMode.CreateNew));
            wav.Write("RIFF"u8); wav.Write(0); wav.Write("WAVEfmt "u8); wav.Write(16);
            wav.Write((short)1); wav.Write((short)2); wav.Write(48000); wav.Write(192000);
            wav.Write((short)4); wav.Write((short)16); wav.Write("data"u8); wav.Write(0);
        }
    }
    internal void Write(TapeBlock block)
    {
        // Roll all tracks together, well before the RIFF size limit.
        if (frames >= 48000L * 3600) ClosePart();
        if (timeline == null) OpenPart();
        if (previous != block.Band)
        {
            timeline!.WriteLine(JsonSerializer.Serialize(new CaptureMark(pairs, frames, block.Utc, block.Band)));
            previous = block.Band;
        }
        iq?.Write(block.Iq);
        if (wav != null)
        {
            var samples = block.Pcm;
            if (block.Channels == 1)
            {
                if (stereo.Length < samples.Length * 2) stereo = new short[samples.Length * 2];
                for (int i = 0; i < samples.Length; i++) stereo[2 * i] = stereo[2 * i + 1] = samples[i];
                wav.Write(MemoryMarshal.AsBytes(stereo.AsSpan(0, samples.Length * 2)));
            }
            else wav.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        }
        pairs += block.Iq.Length / 2;
        frames += block.Pcm.Length / block.Channels;
    }
    private void ClosePart()
    {
        try
        {
            if (wav != null)
            {
                int bytes = checked((int)(wav.BaseStream.Length - 44));
                wav.Seek(4, SeekOrigin.Begin); wav.Write(bytes + 36);
                wav.Seek(40, SeekOrigin.Begin); wav.Write(bytes);
            }
        }
        finally
        {
            try { wav?.Dispose(); } finally { try { iq?.Dispose(); } finally { timeline?.Dispose(); } }
            wav = null; iq = null; timeline = null; previous = null;
            frames = pairs = 0;
        }
    }
    internal string Finish()
    {
        if (part == 0) throw new InvalidOperationException("No received samples to save.");
        ClosePart();
        Directory.Move(working, destination);
        return destination;
    }
    public void Dispose() => ClosePart();
}
