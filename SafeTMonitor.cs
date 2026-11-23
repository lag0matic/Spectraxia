using System.Runtime.InteropServices;

namespace Spectraxia;

internal sealed record SafeTSite(string Name, long Hz)
{
    internal static readonly SafeTSite[] Local = [
        new("Richmond", 853112500), new("Centerville", 774218750), new("Connersville", 853725000),
        new("Brookville", 853887500), new("Dalton", 853387500), new("Union City", 853925000)
    ];
    internal DialSetting Dial => new(Hz, ListenMode.NFM, 12500);
    public override string ToString() => $"{Name} · {Hz / 1e6:0.000##} MHz";
}

[StructLayout(LayoutKind.Sequential)]
internal struct SafeTBlock
{
    public int Messages, SiteReports, SystemId, Rfss, Site, Nac, Talkgroup, GrantCount;
}

internal sealed class SafeTReception(SafeTSite site)
{
    internal SafeTSite Site => site;
    internal volatile int FollowGroup = -1; // -1 monitor only, 0 any group, positive for a selected group.
    internal volatile int VoiceCenter;
    internal volatile string VoiceStatus = "";
    private readonly SortedSet<int> groups = new();
    internal int[] Groups() { lock (gate) return groups.ToArray(); }
    internal void Heard(int group) { lock (gate) groups.Add(group); }
    private readonly object gate = new();
    private readonly Queue<(long Time, int Count)> history = new();
    private int recent, total, reports, talkgroup;
    private SafeTBlock identity;
    private long lastId;
    private string state = "Waiting for receiver";

    internal void Reset(string status)
    {
        lock (gate)
        {
            state = status;
            history.Clear();
            recent = total = reports = talkgroup = 0;
            identity = default;
        }
    }

    private void Expire(long now)
    {
        while (history.TryPeek(out var first) && now - first.Time >= 10000)
            recent -= history.Dequeue().Count;
    }

    internal void Accept(SafeTBlock block, long now)
    {
        if (block.Messages == 0) return;
        lock (gate)
        {
            Expire(now);
            history.Enqueue((now, block.Messages));
            recent += block.Messages;
            total += block.Messages;
            if (block.SiteReports > 0)
            {
                reports += block.SiteReports;
                identity = block;
                lastId = now;
            }
            if (block.Talkgroup != 0) talkgroup = block.Talkgroup;
        }
    }

    internal (int Recent, int Total, int Reports, SafeTBlock Id, string Status) Snapshot(long now)
    {
        lock (gate)
        {
            Expire(now);
            string text = state;
            if (reports > 0)
                text = $"Site {identity.Rfss}-{identity.Site} · system {identity.SystemId:X3} · NAC {identity.Nac:X3} · last ID {(now-lastId)/1000}s ago";
            if (talkgroup != 0) text += $" · last grant TG {talkgroup}";
            return (recent, total, reports, identity, VoiceStatus.Length == 0 ? text : text + " · " + VoiceStatus);
        }
    }
}

// Owned by one USB receive loop. A new site or hardware center needs fresh symbol timing.
internal sealed class SafeTMonitor : IDisposable
{
    internal const string Library = "p25-runtime/spectraxia_p25.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr p25_open(uint rate, int offset);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void p25_feed_grants(IntPtr decoder, [In] byte[] iq, nuint length, out SafeTBlock block,
        [Out] SafeTGrant[] grants, nuint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void p25_close(IntPtr decoder);

    private SafeTReception? current;
    private long center;
    private int rate;
    private IntPtr decoder;
    private readonly SafeTGrant[] grants = new SafeTGrant[16];
    internal SafeTVoice Voice { get; } = new();
    private bool failed;

    internal void Feed(SafeTReception? next, byte[] iq, long actualCenter, int sampleRate, bool settled = true)
    {
        if (next != current || sampleRate != rate)
        {
            Dispose();
            current = next;
            rate = sampleRate;
            failed = false;
            next?.Reset("Listening · waiting for a site ID");
        }
        if (next == null || failed) return;
        try
        {
            if (actualCenter != center || !settled)
            {
                CloseControl();
                center = actualCenter;
            }
            Voice.Advance(next, iq.Length / 2, rate);
            if (!settled) return;
            if (TuningView.Fits(next.Site.Hz, center, rate, ListenMode.NFM, 12500))
            {
                if (decoder == IntPtr.Zero) decoder = p25_open((uint)rate, checked((int)(next.Site.Hz-center)));
                if (decoder == IntPtr.Zero) throw new InvalidOperationException("Unsupported P25 capture rate");
                p25_feed_grants(decoder, iq, (nuint)iq.Length, out var block, grants, (nuint)grants.Length);
                next.Accept(block, Environment.TickCount64);
                for (int i = 0; i < block.GrantCount; i++)
                {
                    next.Heard(grants[i].Group);
                    Voice.Grant(next, grants[i], center, rate);
                }
            }
            Voice.Feed(next, iq, center, rate);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
        {
            Voice.Reset(next);
            failed = true;
            next.Reset("P25 decoder unavailable · check the p25-runtime folder");
        }
    }

    private void CloseControl()
    {
        if (decoder != IntPtr.Zero) p25_close(decoder);
        decoder = IntPtr.Zero;
    }

    public void Dispose()
    {
        CloseControl();
        Voice.Reset(current);
        current?.Reset("Waiting for receiver");
        current = null;
    }

    internal static int Replay(string path, string rateText, string centerText, string siteText, string? groupText = null)
    {
        if (!int.TryParse(rateText, out int rate) || !Radio.SampleRates.Contains(rate) ||
            !long.TryParse(centerText, out long center) || !long.TryParse(siteText, out long hz) ||
            SafeTSite.Local.FirstOrDefault(s => s.Hz == hz) is not {} site ||
            !TuningView.Fits(hz, center, rate, ListenMode.NFM, 12500)) return 2;
        int choice = -1;
        if (groupText != null && (!int.TryParse(groupText,out choice) || choice is < 0 or > 65535)) return 2;
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length == 0 || (file.Length & 1) != 0) return 2;
            using var monitor = new SafeTMonitor();
            var reception = new SafeTReception(site) { FollowGroup = choice };
            var buffer = new byte[32768];
            var heard = new HashSet<string>();
            var pcm = new short[buffer.Length*24000/rate+1];
            while (file.Position < file.Length)
            {
                if (file.Length - file.Position < buffer.Length) Array.Resize(ref buffer, (int)(file.Length-file.Position));
                file.ReadExactly(buffer);
                monitor.Feed(reception, buffer, center, rate);
                if (choice >= 0)
                {
                    monitor.Voice.Render(pcm);
                    if (reception.VoiceStatus.StartsWith("Clear voice") && pcm.Any(s => s != 0))
                        heard.Add(reception.VoiceStatus);
                }
            }
            var result = reception.Snapshot(Environment.TickCount64);
            Console.WriteLine($"{site.Name} · {result.Total} valid blocks · {result.Reports} own-site reports · {result.Status}");
            if (choice >= 0) Console.WriteLine(heard.Count == 0 ? "No confirmed clear PCM in this capture" : string.Join(Environment.NewLine,heard));
            return result.Reports == 0 ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine(ex.Message); return 2; }
    }
}
