using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Spectraxia;

internal sealed class HdReception(long station, long center)
{
    internal long Station => station;
    internal long Center => center;
    internal volatile int Services;
    internal volatile bool Locked;
    internal volatile string Name = "";
    internal volatile string Error = "";
    private readonly short[] ring = new short[48000 * 2 * 2];
    private readonly object audioLock = new();
    private int read, count, selected = -1, phase;
    private short lastLeft, lastRight;
    private bool previous, playing;

    internal void Select(int program)
    {
        lock (audioLock)
        {
            if (program == selected) return;
            selected = program;
            ClearAudio();
        }
    }

    private void ClearAudio()
    {
        count = read = phase = 0;
        playing = previous = false;
    }

    internal void LostSync()
    {
        lock (audioLock)
        {
            Locked = false;
            Services = 0;
            Name = "";
            ClearAudio();
        }
    }

    internal void Audio(int program, IntPtr data, int length, ref short[] buffer)
    {
        lock (audioLock)
        {
            if (selected != program || (Services & (1 << program)) == 0) return;
            if (buffer.Length < length) buffer = new short[length];
            Marshal.Copy(data, buffer, 0, length);
            for (int n = 0; n + 1 < length; n += 2)
            {
                short left = buffer[n], right = buffer[n + 1];
                if (previous)
                {
                    while (phase < 160)
                    {
                        if (count == ring.Length) { read = (read + 2) % ring.Length; count -= 2; }
                        int write = (read + count) % ring.Length;
                        ring[write] = (short)(lastLeft + (left - lastLeft) * phase / 160);
                        ring[write + 1] = (short)(lastRight + (right - lastRight) * phase / 160);
                        count += 2;
                        phase += 147;
                    }
                    phase -= 160;
                }
                previous = true;
                lastLeft = left;
                lastRight = right;
            }
        }
    }

    // Keep the live audio clock and recording frame count even during digital fades.
    internal bool Render(short[] pcm, bool gate, bool mono)
    {
        lock (audioLock)
        {
            if (selected < 0) return false;
            if ((Services & (1 << selected)) == 0)
            {
                ClearAudio();
                Array.Clear(pcm);
                return false;
            }
            if (!playing) playing = count >= 48000 * 2 / 4;
            if (!playing || count < pcm.Length)
            {
                playing = false;
                Array.Clear(pcm);
                return false;
            }
            int first = Math.Min(pcm.Length, ring.Length - read);
            Array.Copy(ring, read, pcm, 0, first);
            Array.Copy(ring, 0, pcm, first, pcm.Length - first);
            read = (read + pcm.Length) % ring.Length;
            count -= pcm.Length;
            if (!gate) Array.Clear(pcm);
            else if (mono)
                for (int n = 0; n < pcm.Length; n += 2)
                    pcm[n] = pcm[n + 1] = (short)((pcm[n] + pcm[n + 1]) / 2);
            return true;
        }
    }
}

internal sealed class HdDecoder : IDisposable
{
    // libnrsc5 v3.2.0, Windows x64 ABI. The event union starts after 8 bytes.
    private enum Event { Sync = 2, LostSync = 3, Audio = 7, AudioService = 14, StationName = 16, AudioServiceDescriptor = 20 }
    private const string Library = "hd-runtime/libnrsc5.dll";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Callback(IntPtr evt, IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nrsc5_open_pipe(out IntPtr radio);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nrsc5_set_callback(IntPtr radio, Callback callback, IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nrsc5_pipe_samples_cs16(IntPtr radio, short[] samples, uint count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nrsc5_close(IntPtr radio);

    private readonly Callback callback;
    private readonly HdReception reception;
    private readonly HdChannel channel;
    private IntPtr handle;
    private short[] audio = [];

    internal HdDecoder(int rate, HdReception reception)
    {
        this.reception = reception;
        channel = new(rate, reception.Station - reception.Center);
        callback = OnEvent;
        if (nrsc5_open_pipe(out handle) != 0) throw new InvalidOperationException("Could not open HD decoder");
        nrsc5_set_callback(handle, callback, IntPtr.Zero);
    }

    internal void Feed(byte[] iq)
    {
        var (samples, count) = channel.Convert(iq);
        if (nrsc5_pipe_samples_cs16(handle, samples, (uint)count) != 0)
            throw new InvalidOperationException("HD decoder rejected I/Q");
    }

    private void OnEvent(IntPtr evt, IntPtr context)
    {
        Event kind = (Event)Marshal.ReadInt32(evt);
        if (kind == Event.Sync) reception.Locked = true;
        else if (kind == Event.LostSync) reception.LostSync();
        else if (kind == Event.StationName)
            reception.Name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(evt, 8))?.Trim() ?? "";
        else if (kind is Event.AudioService or Event.AudioServiceDescriptor)
        {
            int program = Marshal.ReadInt32(evt, 8);
            if ((uint)program >= 8) return;
            int mask = 1 << program;
            reception.Services = Marshal.ReadInt32(evt, 12) == 0 ? reception.Services | mask : reception.Services & ~mask;
        }
        else if (kind == Event.Audio)
        {
            int program = Marshal.ReadInt32(evt, 8);
            long count = Marshal.ReadInt64(evt, 24);
            IntPtr data = Marshal.ReadIntPtr(evt, 16);
            if ((uint)program < 8 && data != IntPtr.Zero && count is > 0 and <= 65536)
                reception.Audio(program, data, (int)count, ref audio);
        }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) nrsc5_close(handle);
        handle = IntPtr.Zero;
        GC.KeepAlive(callback);
    }
}

internal sealed class HdReceiver(int rate) : IDisposable
{
    private readonly record struct Block(HdReception Reception, byte[] Iq);
    private readonly Channel<Block> queue = Channel.CreateBounded<Block>(
        new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
    private Task? worker;
    private volatile HdReception? current;
    private volatile string failure = "";
    internal static bool InBand(long hz) => hz is >= 87_500_000 and <= 108_000_000;
    internal HdReception? Push(byte[] iq, long station, long center, bool valid, int program)
    {
        if (!valid || !InBand(station) || Math.Abs(station - center) + 200_000 > rate / 2)
            return current = null;
        var reception = current;
        if (reception == null || reception.Station != station || reception.Center != center)
            current = reception = new(station, center);
        reception.Select(program);
        if (failure.Length != 0) reception.Error = failure;
        else
        {
            worker ??= Task.Run(Consume);
            if (!queue.Writer.TryWrite(new(reception, iq))) return current = null;
        }
        return reception;
    }

    private async Task Consume()
    {
        HdDecoder? decoder = null;
        HdReception? decoding = null;
        try
        {
            await foreach (var block in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (block.Reception != current) continue;
                if (decoding != block.Reception)
                {
                    decoder?.Dispose();
                    decoding = block.Reception;
                    decoder = new(rate, decoding);
                }
                decoder!.Feed(block.Iq);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
        {
            failure = "HD decoder unavailable";
            if (decoding != null) decoding.Error = failure;
        }
        finally { decoder?.Dispose(); }
    }

    public void Dispose()
    {
        current = null;
        queue.Writer.TryComplete();
        worker?.GetAwaiter().GetResult();
    }
}
