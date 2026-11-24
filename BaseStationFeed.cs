using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Spectraxia;

internal sealed record BaseStationEndpoint(string Host, int Port)
{
    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    internal static BaseStationEndpoint? Parse(string text)
    {
        if (!Uri.TryCreate("tcp://" + text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.PathAndQuery != "/" ||
            uri.Fragment.Length != 0 || uri.Port == 0) return null;
        return new(uri.IdnHost.Trim('[', ']'), uri.Port < 0 ? 30003 : uri.Port);
    }
}

internal sealed record SbsReport(int Address, string Callsign, int? Altitude, int? Speed,
    double? Track, int? Climb, GeoPosition? Position)
{
    internal static SbsReport? Parse(string line)
    {
        var f = line.Split(',');
        if (f.Length < 22 || f[0] != "MSG" || f[1].Length != 1 || f[1][0] is < '1' or > '8' ||
            f[4].Length != 6 || !int.TryParse(f[4], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out int address)) return null;
        static int? Integer(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
        static double? Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            && double.IsFinite(v) ? v : null;
        var lat = Number(f[14]);
        var lon = Number(f[15]);
        var track = Number(f[13]);
        var speed = Number(f[12]);
        return new(address, f[10].Trim(), Integer(f[11]), speed is >= 0 and <= 9999 ? (int)Math.Round(speed.Value) : null,
            track is >= 0 and <= 360 ? track % 360 : null, Integer(f[16]),
            lat is >= -90 and <= 90 && lon is >= -180 and <= 180 ? new(lat.Value, lon.Value) : null);
    }
}

internal static class BaseStationFeed
{
    internal static Task Run(BaseStationEndpoint endpoint, AdsbTraffic traffic, CancellationToken stop)
    {
        var reports = RunConnection(endpoint, line =>
        {
            if (SbsReport.Parse(line) is {} report) traffic.Update(report, Environment.TickCount64);
        }, status => traffic.Status = status, 2000, stop);
        if (endpoint.Port != 30003) return reports;
        var categories = RunConnection(new(endpoint.Host, 30002),
            line => ReadCategory(line, traffic, Environment.TickCount64), null, 15000, stop);
        return Task.WhenAll(reports, categories);
    }

    internal static void ReadCategory(string line, AdsbTraffic traffic, long now)
    {
        int offset = line.Length switch { 30 when line[0] == '*' => 1, 42 when line[0] == '@' => 13, _ => 0 };
        if (offset == 0 || line[^1] != ';') return;
        Span<byte> frame = stackalloc byte[14];
        if (Convert.FromHexString(line.AsSpan(offset, 28), frame, out _, out _) != System.Buffers.OperationStatus.Done)
            return;
        var category = AircraftSymbol.Decode(frame[4] >> 3, frame[4] & 7);
        if (category == EmitterCategory.Unknown || !AdsbDecoder.ValidFrame(frame)) return;
        int address = frame[1] << 16 | frame[2] << 8 | frame[3];
        traffic.SetCategory(address, category, now);
    }

    private static async Task RunConnection(BaseStationEndpoint endpoint, Action<string> onLine,
        Action<string>? status, int retryMs, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                status?.Invoke($"Connecting to {endpoint}");
                try
                {
                    using var client = new TcpClient();
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(8));
                        await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
                    }
                    status?.Invoke($"BaseStation · {endpoint}");
                    await Read(client.GetStream(), onLine, stop).ConfigureAwait(false);
                    status?.Invoke($"Feed closed · retrying {endpoint}");
                }
                catch (Exception ex) when (!stop.IsCancellationRequested && ex is IOException or SocketException or OperationCanceledException)
                {
                    status?.Invoke($"Feed unavailable · retrying {endpoint}");
                }
                await Task.Delay(retryMs, stop).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or IOException) { }
    }

    private static async Task Read(NetworkStream stream, Action<string> onLine, CancellationToken stop)
    {
        var block = new byte[8192];
        var line = new byte[1024];
        int length = 0;
        int count;
        while ((count = await stream.ReadAsync(block, stop).ConfigureAwait(false)) != 0)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = block[i];
                if (b == '\n')
                {
                    if (length > 0)
                    {
                        onLine(Encoding.ASCII.GetString(line, 0, length).TrimEnd('\r'));
                    }
                    length = 0;
                }
                else if (length == line.Length) length = -1; // Discard oversized lines through the next newline.
                else if (length >= 0) line[length++] = b;
            }
        }
    }
}
