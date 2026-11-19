using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Spectraxia;

internal sealed class ReviewMap : Control
{
    private const int Rows = 192, Columns = 768;
    private readonly (TapeBlock Block, float[]? Bins)[] rows = new (TapeBlock, float[]?)[Rows];
    private readonly Bitmap bitmap = new(Columns, Rows, PixelFormat.Format32bppArgb);
    private ReviewClip? clip;
    private int cursor, min, max;
    private long center = -1, station;
    private int rate;
    private bool audioOnly;
    private Rectangle Chart => new(3, Font.Height + 8, Math.Max(1, Width - 6), Math.Max(1, Height - Font.Height - 14));
    internal event Action<double, long?>? Pick;
    internal ReviewMap()
    {
        Dock = DockStyle.Fill; BackColor = Color.FromArgb(12, 18, 22); DoubleBuffered = ResizeRedraw = true;
        MouseDown += (_, e) =>
        {
            var rect = Chart;
            if (clip == null || e.Button != MouseButtons.Left || !rect.Contains(e.Location)) return;
            double seconds = (e.Y - rect.Top) / (double)rect.Height * clip.Duration;
            long hz = center + (long)Math.Round(((e.X - rect.Left) / (double)rect.Width - .5) * rate);
            var b = clip.Blocks[clip.Index(seconds)];
            Pick?.Invoke(seconds, !audioOnly && Math.Abs(hz - b.Band.Center) < b.Band.Rate / 2 ? hz : null);
        };
    }
    internal void Bind(ReviewClip? source, int floor, int ceiling)
    {
        clip = source; center = -1; min = floor; max = ceiling;
        Array.Clear(rows);
        if (clip != null)
        {
            audioOnly = clip.Blocks[0].Iq.Length == 0;
            for (int y = 0; y < Rows; y++)
            {
                var b = clip.Blocks[clip.Index((y + .5) * clip.Duration / Rows)];
                rows[y] = (b, !audioOnly && b.Band.Settled && b.Iq.Length >= 2 * Spectrum.Bins ? Spectrum.Analyze(b.Iq) : null);
            }
        }
        Invalidate();
    }
    internal void Mark(int index, long hz)
    {
        if (clip == null) return;
        cursor = index; station = hz;
        var band = clip.Blocks[index].Band;
        if (center != band.Center || rate != band.Rate)
        {
            center = band.Center; rate = band.Rate;
            Rasterize();
        }
        Invalidate();
    }
    private void Rasterize()
    {
        var pixels = new int[Columns * Rows];
        for (int y = 0; y < Rows; y++)
        {
            var (b, bins) = rows[y];
            if (audioOnly)
            {
                double level = 0;
                foreach (short v in b.Pcm) level += v * (double)v;
                int reach = (int)(Math.Sqrt(level / Math.Max(1, b.Pcm.Length)) / 32768 * Columns / 2);
                for (int x = Columns / 2 - reach; x <= Math.Min(Columns - 1, Columns / 2 + reach); x++) pixels[y * Columns + x] = Color.Turquoise.ToArgb();
                continue;
            }
            if (bins == null) continue;
            double left = center - rate / 2.0 - (b.Band.Center - b.Band.Rate / 2.0);
            for (int x = 0; x < Columns; x++)
            {
                double edge = (left + x * (double)rate / Columns) * Spectrum.Bins / b.Band.Rate;
                int start = (int)Math.Floor(edge);
                if (start < 0 || start >= Spectrum.Bins) continue;
                int end = Math.Min(Spectrum.Bins, (int)Math.Ceiling(edge + (double)rate * Spectrum.Bins / Columns / b.Band.Rate));
                float peak = bins[start];
                for (int k = start + 1; k < end; k++) peak = Math.Max(peak, bins[k]);
                pixels[y * Columns + x] = Spectrum.WaterfallColor(peak, min, max).ToArgb();
            }
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, Columns, Rows), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (clip == null) return;
        var rect = Chart;
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.DrawImage(bitmap, rect);
        var axis = new Rectangle(rect.Left, 1, rect.Width, Font.Height + 4);
        if (audioOnly) TextRenderer.DrawText(e.Graphics, "Recorded audio level", Font, axis, ForeColor, TextFormatFlags.Left);
        else
        {
            void Tick(double hz, TextFormatFlags position) => TextRenderer.DrawText(e.Graphics,
                $"{hz / 1e6:F3} MHz", Font, axis, ForeColor, position | TextFormatFlags.SingleLine);
            Tick(center - rate / 2.0, TextFormatFlags.Left);
            if (rect.Width > 360) Tick(center, TextFormatFlags.HorizontalCenter);
            Tick(center + rate / 2.0, TextFormatFlags.Right);
            for (int i = 1; i < Rows; i++)
            {
                var a = rows[i-1].Block.Band;
                var b = rows[i].Block.Band;
                if (a.Center != b.Center || a.Rate != b.Rate || a.Session != b.Session)
                {
                    int boundary = rect.Top + i * rect.Height / Rows;
                    e.Graphics.DrawLine(Pens.CadetBlue, rect.Left, boundary, rect.Right, boundary);
                }
            }
        }
        string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss.fff");
        TextRenderer.DrawText(e.Graphics, Clock(clip.Blocks[0].Utc), Font, rect.Location,
            Color.WhiteSmoke, Color.FromArgb(22,28,34));
        var last = clip.Blocks[^1];
        TextRenderer.DrawText(e.Graphics, Clock(last.Utc.AddSeconds(last.Seconds)), Font,
            new Point(rect.Left, rect.Bottom - Font.Height), Color.WhiteSmoke, Color.FromArgb(22,28,34));
        int y = rect.Top + (int)(clip.Times[cursor] / clip.Duration * rect.Height);
        e.Graphics.DrawLine(Pens.White, rect.Left, y, rect.Right, y);
        if (!audioOnly)
        {
            int x = rect.Left + (int)((station - center + rate / 2.0) / rate * rect.Width);
            e.Graphics.DrawLine(Pens.Gold, x, rect.Top, x, rect.Bottom);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) bitmap.Dispose(); base.Dispose(disposing); }
}
