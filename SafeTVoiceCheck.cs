using System.Runtime.InteropServices;

namespace Spectraxia;

internal static class SafeTVoiceCheck
{
    // Real Richmond control/traffic capture and the public 96 kS/s Motorola XTS5000 voice recording.
    internal static int Run(string richmond, string xts)
    {
        var site = SafeTSite.Local[0];
        var reception = new SafeTReception(site) { FollowGroup = 12436 };
        using var monitor = new SafeTMonitor();
        bool followed = false, skipped = false;
        using (var file = File.OpenRead(richmond))
        {
            var block = new byte[32768];
            var audio = new short[328];
            while (file.Position < file.Length)
            {
                if (file.Length-file.Position < block.Length) Array.Resize(ref block,(int)(file.Length-file.Position));
                file.ReadExactly(block);
                monitor.Feed(reception,block,852200000,2400000);
                followed |= monitor.Voice.Frequency == 852250000;
                skipped |= reception.VoiceStatus.Contains("Encrypted");
                monitor.Voice.Render(audio);
                if (Array.Exists(audio, x => x != 0)) throw new InvalidDataException("Unexpected PCM from Richmond encrypted fixture");
            }
        }
        Console.WriteLine($"Richmond TG 12436: resolved 852.250 MHz {followed}, encrypted skip {skipped}, site reports {reception.Snapshot(Environment.TickCount64).Reports}");
        var floats = MemoryMarshal.Cast<byte,float>(File.ReadAllBytes(xts));
        float peak = 0;
        foreach (float sample in floats) peak = Math.Max(peak, Math.Abs(sample));
        byte[] iq = new byte[floats.Length];
        // Place the recorded baseband carrier at +24 kHz in the simulated retuned capture.
        for (int i=0; i<iq.Length; i+=2)
        {
            var (x,y) = (floats[i],floats[i+1]);
            var (a,b) = ((i/2)%4) switch { 1 => (-y,x), 2 => (-x,-y), 3 => (y,-x), _ => (x,y) };
            iq[i] = (byte)Math.Clamp((int)Math.Round(127.5 + 120*a/peak),0,255);
            iq[i+1] = (byte)Math.Clamp((int)Math.Round(127.5 + 120*b/peak),0,255);
        }
        bool clear = CheckCall(iq,1,true) && CheckCall(iq,2,false);
        return followed && skipped && clear ? 0 : 1;
    }

    private static bool CheckCall(byte[] recording, int group, bool expectAudio)
    {
        const int rate = 96000, hz = 852250000;
        var owner = new SafeTReception(SafeTSite.Local[0]) { FollowGroup = group };
        var voice = new SafeTVoice();
        try
        {
            voice.Grant(owner,new SafeTGrant {Group=group+10,Hz=hz,Nac=0x293},853112500,rate);
            if (voice.Frequency != 0) return false;
            voice.Grant(owner,new SafeTGrant {Group=group,Hz=hz,Nac=0x293},853112500,rate);
            int actualCenter = owner.VoiceCenter;
            if (actualCenter != hz-24000) return false;
            int nonzero = 0;
            bool heard = false;
            // The source starts with about two seconds of idle RF before the transmitter keys.
            for (int i=2*rate*2; i<recording.Length; i+=2048)
            {
                byte[] iq = recording.AsSpan(i,Math.Min(2048,recording.Length-i)).ToArray();
                voice.Advance(owner,iq.Length/2,rate);
                voice.Feed(owner,iq,actualCenter,rate);
                heard |= owner.VoiceStatus.StartsWith("Clear voice");
                var pcm = new short[iq.Length/4];
                voice.Render(pcm);
                nonzero += pcm.Count(x=>x != 0);
            }
            voice.Advance(owner,rate*3,rate);
            Console.WriteLine($"XTS5000 TG {group}: clear {heard}, {nonzero} nonzero 48 kHz samples, control resumed {owner.VoiceCenter==0}");
            return heard == expectAudio && (expectAudio ? nonzero > 48000 : nonzero == 0) && owner.VoiceCenter == 0;
        }
        finally { voice.Reset(owner); }
    }
}
