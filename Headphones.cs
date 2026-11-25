using System.Runtime.InteropServices;

namespace Spectraxia;

internal sealed class Headphones : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Format { public ushort Tag, Channels; public uint Rate, Bytes; public ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Caps { public ushort Manufacturer, Product; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }
    [DllImport("winmm")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm", CharSet = CharSet.Unicode)] private static extern int waveOutGetDevCapsW(UIntPtr id, out Caps caps, uint size);
    [DllImport("winmm")] private static extern int waveOutOpen(out IntPtr handle, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm")] private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr hdr, int size);
    [DllImport("winmm")] private static extern int waveOutWrite(IntPtr handle, IntPtr hdr, int size);
    [DllImport("winmm")] private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr hdr, int size);
    [DllImport("winmm")] private static extern int waveOutReset(IntPtr handle);
    [DllImport("winmm")] private static extern int waveOutClose(IntPtr handle);
    private readonly IntPtr handle;
    private static readonly int Size = Marshal.SizeOf<Header>();
    private readonly Queue<IntPtr> pending = new();
    private readonly int channels;
    internal long DroppedFrames { get; private set; }
    internal int EmptyQueueEvents { get; private set; }
    private bool started;
    internal sealed record Endpoint(uint Id, string Name) { public override string ToString() => Name; }
    internal static IEnumerable<Endpoint> Outputs()
    {
        yield return new(uint.MaxValue, "System default");
        for (uint i = 0; i < waveOutGetNumDevs(); i++)
            if (waveOutGetDevCapsW(i, out var caps, (uint)Marshal.SizeOf<Caps>()) == 0)
                yield return new(i, caps.Name);
    }
    internal Headphones(uint id, bool stereo = false)
    {
        ushort channels = (ushort)(stereo ? 2 : 1);
        this.channels = channels;
        var format = new Format { Tag = 1, Channels = channels, Rate = 48000, Bytes = (uint)(96000*channels), Align = (ushort)(2*channels), Bits = 16 };
        Radio.Check(waveOutOpen(out handle, id, ref format, IntPtr.Zero, IntPtr.Zero, 0), "Open audio output");
    }
    private short[] scaled = [];
    internal void WaitForRoom(CancellationToken token)
    {
        while (pending.Count >= 4 && (Marshal.PtrToStructure<Header>(pending.Peek()).Flags & 1) == 0)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(2);
        }
    }
    internal void Play(short[] samples, int volume = 100)
    {
        while (pending.TryPeek(out var head) && (Marshal.PtrToStructure<Header>(head).Flags & 1) != 0)
        {
            Release(head);
            pending.Dequeue();
        }
        if (samples.Length == 0) return;
        if (pending.Count >= 12) { DroppedFrames += samples.Length / channels; return; }
        if (started && pending.Count == 0) EmptyQueueEvents++;
        var pcm = Marshal.AllocHGlobal(samples.Length * 2);
        var hdr = Marshal.AllocHGlobal(Size);
        bool prepared = false;
        try
        {
            if (volume != 100)
            {
                if (scaled.Length < samples.Length) scaled = new short[samples.Length];
                for (int i=0; i<samples.Length; i++) scaled[i] = (short)(samples[i] * volume / 100);
                Marshal.Copy(scaled, 0, pcm, samples.Length);
            }
            else Marshal.Copy(samples, 0, pcm, samples.Length);
            Marshal.StructureToPtr(new Header { Data = pcm, Length = (uint)samples.Length * 2 }, hdr, false);
            Radio.Check(waveOutPrepareHeader(handle, hdr, Size), "Prepare audio");
            prepared = true;
            Radio.Check(waveOutWrite(handle, hdr, Size), "Play audio");
            pending.Enqueue(hdr);
            started = true;
        }
        catch
        {
            if (prepared) waveOutUnprepareHeader(handle, hdr, Size);
            Marshal.FreeHGlobal(pcm);
            Marshal.FreeHGlobal(hdr);
            throw;
        }
    }
    private void Release(IntPtr hdr)
    {
        Radio.Check(waveOutUnprepareHeader(handle, hdr, Size), "Release audio buffer");
        Marshal.FreeHGlobal(Marshal.PtrToStructure<Header>(hdr).Data);
        Marshal.FreeHGlobal(hdr);
    }
    internal void Drain(CancellationToken token = default)
    {
        long deadline = Environment.TickCount64 + 2000;
        while (pending.TryPeek(out var head))
        {
            token.ThrowIfCancellationRequested();
            if ((Marshal.PtrToStructure<Header>(head).Flags & 1) != 0)
            {
                Release(head);
                pending.Dequeue();
            }
            else if (Environment.TickCount64 > deadline) throw new IOException("Audio output did not drain.");
            else Thread.Sleep(2);
        }
    }
    public void Dispose()
    {
        waveOutReset(handle);
        IOException? failure = null;
        while (pending.TryDequeue(out var header))
        {
            try { Release(header); }
            catch (IOException ex) { failure ??= ex; }
        }
        int closed = waveOutClose(handle);
        if (failure != null) throw failure;
        Radio.Check(closed, "Close audio output");
    }
}

// Owned by the receiver worker. Audio device errors must not escape into the RTL callback.
internal sealed class LiveAudioOutput(uint endpoint, Func<uint, bool, Headphones>? open = null) : IDisposable
{
    private Headphones? sink;
    private bool stereo;
    private long retryAfter;
    internal string? Error { get; private set; }
    internal int EmptyQueueEvents => sink?.EmptyQueueEvents ?? 0;
    internal long DroppedFrames => sink?.DroppedFrames ?? 0;

    internal void Play(short[] pcm, int volume, bool enabled, bool useStereo, long now)
    {
        if (stereo != useStereo || !enabled)
        {
            Dispose();
            stereo = useStereo;
            retryAfter = 0;
        }
        if (!enabled || now < retryAfter) return;
        try
        {
            sink ??= open == null ? new Headphones(endpoint, stereo) : open(endpoint, stereo);
            sink.Play(pcm, volume);
            Error = null;
        }
        catch (IOException ex)
        {
            Dispose();
            Error = ex.Message;
            retryAfter = now + 1500;
        }
    }

    public void Dispose()
    {
        var previous = sink;
        sink = null;
        try { previous?.Dispose(); }
        catch (IOException ex) { Error = ex.Message; }
    }
}
