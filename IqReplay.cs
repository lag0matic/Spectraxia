using System.Diagnostics;

namespace Spectraxia;

internal static class IqReplay
{
    internal static int Run(string[] args)
    {
        try
        {
            int rate = int.Parse(args[2]);
            if (!Radio.SampleRates.Contains(rate)) throw new ArgumentException("Unsupported capture rate.");
            if (args[0] == "--capture-iq")
            {
                long center = long.Parse(args[3]);
                int seconds = int.Parse(args[4]);
                if (seconds is < 1 or > 60 || center < 0 || center > TuningView.MaxHz) throw new ArgumentOutOfRangeException();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 10));
                using var file = new FileStream(args[1], FileMode.CreateNew);
                var config = new ReceiverSettings(SampleRate: rate);
                long remaining = 2L * rate * seconds;
                Radio.Receive(center, config, () => config, () => center, _ => {}, stop.Token, (iq, _, _) =>
                {
                    int bytes = (int)Math.Min(iq.Length, remaining);
                    file.Write(iq, 0, bytes);
                    remaining -= bytes;
                    if (remaining == 0) stop.Cancel();
                });
                if (remaining != 0) throw new IOException("Capture stopped early.");
                return 0;
            }
            var mode = Enum.Parse<ListenMode>(args[3], true);
            if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown mode.");
            int offset = int.Parse(args[4]);
            int next = args.Length > 6 && !args[6].StartsWith("--") ? int.Parse(args[6]) : offset;
            var settings = new ReceiverSettings(Mode: mode, SampleRate: rate, ForceMono: args.Contains("--mono"),
                RemoveDc: args.Contains("--remove-dc"));
            if (!TuningView.Fits(offset,0,rate,mode,settings.Width) || !TuningView.Fits(next,0,rate,mode,settings.Width))
                throw new ArgumentException("Station outside recorded span.");
            using var input = File.OpenRead(args[1]);
            if (input.Length == 0 || input.Length % 2 != 0) throw new InvalidDataException("Expected interleaved unsigned 8-bit I/Q pairs.");
            int channels = mode is ListenMode.IQ or ListenMode.WFM ? 2 : 1;
            long expected = input.Length/2 * 48000 / rate;
            long bytesOut = expected * channels * 2;
            if (bytesOut > uint.MaxValue - 36) throw new IOException("Output exceeds RIFF size.");
            using var writer = new BinaryWriter(new FileStream(args[5], FileMode.CreateNew));
            writer.Write("RIFF"u8); writer.Write((uint)(36+bytesOut)); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)channels); writer.Write(48000);
            writer.Write(96000*channels); writer.Write((short)(2*channels)); writer.Write((short)16);
            writer.Write("data"u8); writer.Write((uint)bytesOut);
            var decoder = new ReceiverAudio(rate);
            using var sink = args.Contains("--check-output") ? new Headphones(uint.MaxValue, channels == 2) : null;
            var timer = Stopwatch.StartNew();
            long frames = 0, lockedFrames = 0;
            string lastName = "", lastText = "";
            var block = new byte[32768];
            while (input.Position < input.Length)
            {
                int selected = input.Position < input.Length/2 ? offset : next;
                int n = (int)Math.Min(block.Length, input.Length-input.Position);
                if (block.Length != n) Array.Resize(ref block,n);
                input.ReadExactly(block);
                var pcm = decoder.Decode(block, 10_000_000, 10_000_000L + selected, settings, 100);
                if (decoder.StereoLocked) lockedFrames += pcm.Length/channels;
                if (lastName != decoder.StationName || lastText != decoder.RadioText)
                {
                    lastName = decoder.StationName; lastText = decoder.RadioText;
                    Console.WriteLine($"{frames/48000.0:F2}s PS [{lastName}] RadioText [{lastText}]");
                }
                writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan()));
                if (sink != null)
                {
                    int delay = (int)(frames/48.0 - 25 - timer.Elapsed.TotalMilliseconds);
                    if (delay > 0) Thread.Sleep(delay);
                    Array.Clear(pcm);
                    sink.Play(pcm);
                }
                frames += pcm.Length / channels;
            }
            sink?.Drain();
            if (mode == ListenMode.WFM) Console.WriteLine($"Pilot lock {lockedFrames / 48000.0:F2} seconds. Final decoder saw {decoder.TextGroups} validated RadioText group observations across timing candidates.");
            Console.WriteLine($"I/Q pairs {input.Length/2}; expected PCM frames {expected}; emitted {frames}.");
            if (sink != null) Console.WriteLine($"Silent waveOut check: dropped {sink.DroppedFrames} frames; queue observed empty {sink.EmptyQueueEvents} times.");
            return frames == expected && (sink?.DroppedFrames ?? 0) == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
