using System.Runtime.InteropServices;

namespace Spectraxia;

[StructLayout(LayoutKind.Sequential)]
internal struct SafeTGrant { public int Group, Hz, Nac, Encrypted; }

// Owned by the RTL sample thread. Timeouts follow capture time so replay behaves like live RF.
internal sealed class SafeTVoice
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Result { public int Samples, Frames, Ended, Encrypted; }
    [DllImport(SafeTMonitor.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr p25_voice_open(uint rate, int offset, ushort group, ushort nac);
    [DllImport(SafeTMonitor.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void p25_voice_feed(IntPtr decoder, byte[] iq, nuint length, [Out] short[] output, nuint capacity, out Result result);
    [DllImport(SafeTMonitor.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void p25_voice_close(IntPtr decoder);

    private SafeTGrant call;
    private IntPtr decoder;
    private long samples, started, activity, decoderCenter;
    private readonly Dictionary<(int Group,int Hz),long> skipUntil = new();
    private readonly short[] decoded = new short[3200];
    private readonly Queue<short> audio = new();
    private short previous;
    private bool confirmed;
    private bool primed;
    private int audioGroup;
    internal int Frequency => call.Hz;

    internal void Advance(SafeTReception owner, int count, int rate)
    {
        samples += count;
        int choice = owner.FollowGroup;
        if (choice < 0 || choice > 0 && choice != audioGroup) audio.Clear();
        if (call.Hz == 0) return;
        if (choice < 0 || choice > 0 && choice != call.Group)
            Finish(owner, "Monitoring control channel");
        else if (confirmed ? samples-activity > rate : samples-started > rate*2L)
        {
            skipUntil[(call.Group,call.Hz)] = samples + rate;
            Finish(owner, confirmed ? "Call ended" : "No confirmed clear voice");
        }
    }

    internal void Grant(SafeTReception owner, SafeTGrant grant, long center, int rate)
    {
        if (grant.Encrypted != 0)
        {
            skipUntil[(grant.Group,grant.Hz)] = samples + rate*3L;
            if (call.Group == grant.Group && call.Hz == grant.Hz) Finish(owner, "Encrypted call skipped");
            else if (call.Hz == 0 && (owner.FollowGroup == 0 || owner.FollowGroup == grant.Group))
                owner.VoiceStatus = $"Encrypted grant skipped · TG {grant.Group}";
            return;
        }
        int choice = owner.FollowGroup;
        if (call.Hz != 0 || choice < 0 || choice > 0 && choice != grant.Group ||
            grant.Hz == owner.Site.Hz || skipUntil.GetValueOrDefault((grant.Group,grant.Hz)) > samples) return;
        if (skipUntil.Count > 64)
            foreach (var old in skipUntil.Where(x => x.Value <= samples).ToArray()) skipUntil.Remove(old.Key);
        call = grant;
        started = activity = samples;
        confirmed = false;
        primed = false;
        audio.Clear();
        previous = 0;
        audioGroup = grant.Group;
        owner.VoiceStatus = $"Following TG {grant.Group} · {grant.Hz/1e6:0.000##} MHz";
        if (!TuningView.Fits(grant.Hz,center,rate,ListenMode.NFM,12500))
            owner.VoiceCenter = grant.Hz - Math.Min(60000,rate/4);
    }

    internal void Feed(SafeTReception owner, byte[] iq, long center, int rate)
    {
        if (call.Hz == 0 || !TuningView.Fits(call.Hz,center,rate,ListenMode.NFM,12500)) return;
        if (decoder == IntPtr.Zero || decoderCenter != center)
        {
            CloseDecoder();
            decoder = p25_voice_open((uint)rate, checked((int)(call.Hz-center)), (ushort)call.Group, (ushort)call.Nac);
            if (decoder == IntPtr.Zero) throw new InvalidOperationException("Unsupported P25 voice rate");
            decoderCenter = center;
        }
        p25_voice_feed(decoder, iq, (nuint)iq.Length, decoded, (nuint)decoded.Length, out var result);
        if (result.Frames > 0) activity = samples;
        if (result.Samples > 0)
        {
            if (!confirmed) owner.VoiceStatus = $"Clear voice · TG {call.Group} · {call.Hz/1e6:0.000##} MHz";
            confirmed = true;
            // IMBE gives 160 samples at 8 kHz per frame. The normal audio/recording path is 48 kHz.
            for (int i = 0; i < result.Samples; i++)
            {
                int delta = decoded[i] - previous;
                for (int j = 1; j <= 6; j++) audio.Enqueue((short)(previous + delta*j/6));
                previous = decoded[i];
            }
            while (audio.Count > 24000) audio.Dequeue();
        }
        if (result.Ended != 0)
        {
            skipUntil[(call.Group,call.Hz)] = samples + (result.Encrypted != 0 ? 3L*rate : rate/4);
            Finish(owner, result.Encrypted != 0 ? "Encrypted call skipped" : "Call ended");
        }
    }

    internal void Render(short[] pcm)
    {
        if (!primed && call.Hz != 0 && audio.Count < 1920) { Array.Clear(pcm); return; }
        if (audio.Count > 0) primed = true;
        int i = 0;
        while (i < pcm.Length && audio.TryDequeue(out var value)) pcm[i++] = value;
        Array.Clear(pcm,i,pcm.Length-i);
    }

    private void CloseDecoder()
    {
        if (decoder != IntPtr.Zero) p25_voice_close(decoder);
        decoder = IntPtr.Zero;
    }

    private void Finish(SafeTReception owner, string status)
    {
        CloseDecoder();
        call = default;
        owner.VoiceCenter = 0;
        owner.VoiceStatus = status;
    }

    internal void Reset(SafeTReception? owner)
    {
        if (owner != null) Finish(owner,"");
        audio.Clear();
        skipUntil.Clear();
        samples = 0;
    }
}
