using System.Numerics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Spectraxia;

internal sealed class Spectrum : Control
{
    internal const int Bins = 2048;
    private readonly Bitmap history = new(Bins, 512, PixelFormat.Format8bppIndexed);
    private readonly long[] rowCenters = new long[512];
    private readonly byte[] indices = new byte[Bins];
    private readonly byte[] rawRows = new byte[Bins * 512];
    private Bitmap? reduced;
    private byte[] reducedRow = [];
    private int[] binEdges = [];
    private void ReduceRow(int y, BitmapData data, int destinationRow)
    {
        int width = reducedRow.Length;
        for (int x = 0; x < width; x++)
        {
            int start = y * Bins + binEdges[x];
            int end = y * Bins + binEdges[x+1];
            byte peak = rawRows[start];
            for (int bin = start + 1; bin < end; bin++) peak = Math.Max(peak, rawRows[bin]);
            reducedRow[x] = peak;
        }
        Marshal.Copy(reducedRow, 0, data.Scan0 + destinationRow * data.Stride, width);
    }
    private Bitmap Raster(int width)
    {
        if (width >= Bins)
        {
            reduced?.Dispose();
            reduced = null;
            return history;
        }
        if (reduced?.Width == width) return reduced;
        reduced?.Dispose();
        reduced = new Bitmap(width, history.Height, history.PixelFormat);
        reduced.Palette = history.Palette;
        reducedRow = new byte[width];
        binEdges = new int[width+1];
        for (int x=0; x<=width; x++) binEdges[x] = x * Bins / width;
        // A smooth drag can change the width every frame. Lock the history once per rebuild.
        var data = reduced.LockBits(new Rectangle(0,0,width,history.Height), ImageLockMode.WriteOnly, reduced.PixelFormat);
        try
        {
            for (int n=0; n<count; n++)
            {
                int y=(firstRow+n) % history.Height;
                ReduceRow(y,data,y);
            }
        }
        finally { reduced.UnlockBits(data); }
        return reduced;
    }
    private readonly float[] average = new float[Bins], peaks = new float[Bins];
    private readonly long[] heldAt = new long[Bins];
    private long lastFrame;
    internal bool Averaging, PeakHold;
    internal int HoldSeconds = 3;
    private bool resetPeaks = true;
    private double split = .5;
    private bool resizing;
    internal int FilterHz = 200000;
    internal ListenMode Mode;
    private int movingEdge;
    internal event Action<int>? ResizeBandwidth;
    private float EdgeX(int side) => (float)(View.Station + (side < 0 ? Listening.Edges(Mode, FilterHz).Low : Listening.Edges(Mode, FilterHz).High) - View.Left) * (Width - 1f) / View.Span;
    private int HitEdge(int x)
    {
        if (count == 0) return 0;
        if (Mode != ListenMode.USB && Math.Abs(x - EdgeX(-1)) <= 5) return -1;
        return Mode != ListenMode.LSB && Math.Abs(x - EdgeX(1)) <= 5 ? 1 : 0;
    }
    private int Divider => (int)Math.Round(Height * split);
    private float[]? latest;
    private int firstRow, count;
    private static readonly Color[] Colors = MakePalette();
    private static Color[] MakePalette()
    {
        // Positions are fractions of the user's Min/Max range. Leave room at the
        // bottom for noise texture, with a short white transition into yellow.
        (int Index, Color Color)[] stops = [
            (0, Color.FromArgb(0, 0, 14)), (37, Color.FromArgb(0, 0, 57)),
            (66, Color.FromArgb(0, 0, 120)), (91, Color.FromArgb(0, 52, 231)),
            (113, Color.FromArgb(54, 156, 255)), (139, Color.FromArgb(216, 236, 255)),
            (148, Color.FromArgb(255, 255, 242)), (165, Color.FromArgb(255, 255, 92)),
            (194, Color.FromArgb(254, 207, 46)), (222, Color.FromArgb(254, 138, 48)),
            (244, Color.FromArgb(254, 83, 18)), (255, Color.FromArgb(255, 32, 8)) ];
        return Enumerable.Range(0, 256).Select(index =>
        {
            int i = Array.FindIndex(stops, 1, stop => stop.Index >= index);
            var lower = stops[i-1]; var upper = stops[i];
            double t = (index-lower.Index) / (double)(upper.Index-lower.Index);
            int Mix(int a, int b) => (int)Math.Round(a + (b-a)*t);
            var a = lower.Color; var b = upper.Color;
            return Color.FromArgb(Mix(a.R,b.R), Mix(a.G,b.G), Mix(a.B,b.B));
        }).ToArray();
    }
    private long center;
    internal TuningView View = new(101_300_000);
    private Point? pressed;
    private long dragLeft;
    private long dragStation;
    private bool panOnly;
    private bool dragged;
    private int wheelDelta;
    internal event Action<long>? Tune;
    internal event Action<long, long>? DragTune;
    internal event Action<int>? WheelTune;
    internal IReadOnlyList<StationMemory> Memories = [];
    internal ManualNotch[] Notches = [];
    internal bool BypassNotches;
    private void DrawNotches(Graphics g)
    {
        foreach (var n in Notches)
        {
            long offset = n.Hz - View.Left;
            if (offset < 0 || offset > View.Span) continue;
            int x = (int)(offset * (Width - 1) / View.Span);
            int w = Math.Max(2, (int)((long)n.WidthHz * (Width - 1) / View.Span));
            bool enabled = n.Enabled && !BypassNotches;
            Color color = enabled ? Color.Salmon : Color.Gray;
            using var pen = new Pen(color) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var shade = new SolidBrush(Color.FromArgb(36, color));
            int top = Math.Max(0, Divider - 42 - Font.Height);
            g.FillRectangle(shade, x - w / 2, 0, w, top);
            g.DrawLine(pen, x, 0, x, top);
            g.DrawLine(pen, x - 4, top - 5, x, top);
            g.DrawLine(pen, x, top, x + 4, top - 5);
        }
    }
    internal event Action<StationMemory>? RecallMemory;
    private readonly List<(Rectangle Bounds, StationMemory Memory)> markers = [];
    private StationMemory? MarkerAt(Point point) => markers.Find(m => m.Bounds.Contains(point)).Memory;
    private StationMemory? pressedMarker;

    private void DrawMarkers(Graphics graphics)
    {
        markers.Clear();
        int height = Font.Height + 4;
        int rows = Math.Clamp((Divider - Font.Height - 30) / height, 0, 3);
        if (rows == 0) return;
        int[] rowEnds = new int[rows];
        using var background = new SolidBrush(Color.FromArgb(33, 55, 65));
        foreach (var memory in Memories)
        {
            long offset = memory.Dial.Hz - View.Left;
            if (offset < 0 || offset > View.Span) continue;
            int x = (int)(offset * (Width - 1) / View.Span);
            int w = Math.Min(Math.Min(160, Width), TextRenderer.MeasureText(memory.Name, Font).Width + 6);
            int left = Math.Clamp(x - w / 2, 0, Width - w);
            int row = Array.FindIndex(rowEnds, end => end <= left);
            if (row < 0) continue;
            var rect = new Rectangle(left, 2 + row * height, w, height - 1);
            rowEnds[row] = rect.Right + 3;
            graphics.FillRectangle(background, rect);
            TextRenderer.DrawText(graphics, memory.Name, Font, rect, Color.PaleTurquoise,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            graphics.DrawLine(Pens.PaleTurquoise, x, rect.Bottom, x, rect.Bottom + 4);
            markers.Add((rect, memory));
        }
    }
    internal Spectrum()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetWaterfallLimits(-85, -22);
        BackColor = Color.FromArgb(12, 18, 22);
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || Width < 2) return;
            Focus();
            Capture = true;
            pressedMarker = (ModifierKeys & Keys.Shift) == 0 ? MarkerAt(e.Location) : null;
            if (pressedMarker != null) return;
            if (Math.Abs(e.Y - Divider) <= 5)
            {
                resizing = true;
                Cursor = Cursors.HSplit;
                return;
            }
            pressed = e.Location;
            movingEdge = (ModifierKeys & Keys.Shift) == 0 ? HitEdge(e.X) : 0;
            dragLeft = View.Left;
            dragStation = View.Station;
            panOnly = (ModifierKeys & Keys.Shift) != 0;
            dragged = false;
        };
        void UpdateDrag(MouseEventArgs e)
        {
            if (resizing)
            {
                split = Math.Clamp(e.Y / (double)Math.Max(1, Height), .15, .85);
                Invalidate();
                return;
            }
            if (pressed == null) Cursor = MarkerAt(e.Location) != null ? Cursors.Hand : Math.Abs(e.Y - Divider) <= 5 ? Cursors.HSplit :
                HitEdge(e.X) != 0 ? Cursors.SizeWE : Cursors.Default;
            if (pressed is not Point start) return;
            int dx = e.X - start.X;
            if (movingEdge != 0)
            {
                if (dx != 0 || dragged)
                {
                    dragged = true;
                    long hz = View.FrequencyAt(e.X / (double)Math.Max(1, Width - 1));
                    var range = Listening.Range(Mode);
                    double width = (hz - View.Station) * movingEdge * (Mode is ListenMode.USB or ListenMode.LSB ? 1 : 2);
                    ResizeBandwidth?.Invoke(Math.Clamp((int)Math.Round(width / range.Increment) * range.Increment, range.Min, range.Max));
                }
                return;
            }
            if (!dragged && Math.Abs(dx) < SystemInformation.DragSize.Width / 2 &&
                Math.Abs(e.Y - start.Y) < SystemInformation.DragSize.Height / 2) return;
            dragged = true;
            long deltaHz = -(long)Math.Round(dx * (double)View.Span / Math.Max(1, Width - 1));
            if (panOnly)
            {
                View.Pan(dragLeft + deltaHz);
                Invalidate();
            }
            else if (dx != 0 || View.Station != dragStation)
                DragTune?.Invoke(dragStation + deltaHz, dragStation - dragLeft);
        }
        MouseMove += (_, e) => UpdateDrag(e);
        MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && pressedMarker is { } memory)
            {
                Capture = false;
                if (MarkerAt(e.Location)?.Id == memory.Id) RecallMemory?.Invoke(memory);
                return;
            }
            if (resizing && e.Button == MouseButtons.Left)
            {
                UpdateDrag(e);
                resizing = false;
                Capture = false;
                return;
            }
            if (e.Button != MouseButtons.Left || pressed == null) return;
            UpdateDrag(e);
            bool tune = !dragged && movingEdge == 0 && count > 0;
            pressed = null;
            Capture = false;
            if (tune) Tune?.Invoke(View.FrequencyAt(Math.Clamp(e.X / (double)Math.Max(1, Width - 1), 0, 1)));
        };
        MouseCaptureChanged += (_, _) => { if (!Capture) { pressedMarker = null; pressed = null; resizing = false; movingEdge = 0; } };
        MouseWheel += (_, e) =>
        {
            wheelDelta += e.Delta;
            int steps = wheelDelta / 120;
            wheelDelta %= 120;
            if (steps != 0) WheelTune?.Invoke(steps);
        };
    }

    internal static float[] Analyze(byte[] iq, float[]? filtered = null)
    {
        var a = new Complex[Bins];
        for (int i = 0, j = 0; i < Bins; i++)
        {
            double window = .5 - .5 * Math.Cos(2 * Math.PI * i / Bins);
            a[j] = (filtered == null ? new Complex(iq[2*i] - 127.5, iq[2*i+1] - 127.5)
                : new Complex(filtered[2*i], filtered[2*i+1])) * window;
            int bit = Bins >> 1;
            while (bit > 0 && (j & bit) != 0) { j ^= bit; bit >>= 1; }
            j ^= bit;
        }
        TransformBitReversed(a);
        return Enumerable.Range(0, Bins).Select(i => (float)Math.Clamp(
            20 * Math.Log10(Math.Max(1e-9, a[(i + Bins/2) % Bins].Magnitude / (127.5 * Bins / 2))), -95, 0)).ToArray();
    }

    internal static void TransformBitReversed(Complex[] a)
    {
        for (int size = 2; size <= a.Length; size *= 2)
        {
            var rotation = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
            for (int start = 0; start < a.Length; start += size)
            {
                var weight = Complex.One;
                for (int k = 0; k < size / 2; k++)
                {
                    int x = start + k, y = x + size / 2;
                    var v = weight * a[y];
                    a[y] = a[x] - v;
                    a[x] += v;
                    weight *= rotation;
                }
            }
        }
    }

    internal static Color WaterfallColor(float db, int min, int max) =>
        Colors[Math.Clamp((int)((db - min) * 255 / (max - min)), 0, 255)];

    internal void ClearPeaks()
    {
        resetPeaks = true;
        Invalidate();
    }

    internal void ChangeCapture(int sampleRate)
    {
        View.ChangeCapture(sampleRate);
        count = 0;
        latest = null;
        Invalidate();
    }

    internal void SetWaterfallLimits(int min, int max)
    {
        var palette = history.Palette;
        for (int i = 0; i < palette.Entries.Length; i++)
            palette.Entries[i] = Colors[Math.Clamp((int)((i * 95.0 / 255 - 95 - min) * 255 / (max - min)), 0, 255)];
        history.Palette = palette;
        if (reduced != null) reduced.Palette = palette;
        Invalidate();
    }

    internal static float NextPeak(float previous, float live, long held, long last, long now, int seconds)
    {
        double decay = 6 * Math.Max(0L, now - Math.Max(last, held + seconds * 1000L)) / 1000.0;
        return Math.Max(live, (float)(previous - decay));
    }

    internal void Add(long hz, float[] row)
    {
        bool newTrace = count == 0 || center != hz;
        center = hz;
        latest = row;
        firstRow = (firstRow + history.Height - 1) % history.Height;
        rowCenters[firstRow] = hz;
        long now = Environment.TickCount64;
        for (int x = 0; x < Bins; x++)
        {
            indices[x] = (byte)Math.Clamp((int)Math.Round((row[x] + 95) * (255.0/95)), 0, 255);
            average[x] = newTrace ? row[x] : average[x] + .22f * (row[x] - average[x]);
            if (resetPeaks || newTrace)
                peaks[x] = row[x];
            else
                peaks[x] = NextPeak(peaks[x], row[x], heldAt[x], lastFrame, now, HoldSeconds);
            if (row[x] >= peaks[x]) heldAt[x] = now;
        }
        lastFrame = now;
        resetPeaks = false;
        var data = history.LockBits(new Rectangle(0, firstRow, Bins, 1), ImageLockMode.WriteOnly, history.PixelFormat);
        try { Marshal.Copy(indices, 0, data.Scan0, Bins); }
        finally { history.UnlockBits(data); }
        indices.CopyTo(rawRows, firstRow * Bins);
        if (reduced != null)
        {
            var strip = reduced.LockBits(new Rectangle(0,firstRow,reduced.Width,1), ImageLockMode.WriteOnly, reduced.PixelFormat);
            try { ReduceRow(firstRow,strip,0); }
            finally { reduced.UnlockBits(strip); }
        }
        count = Math.Min(history.Height, count + 1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Height < 50 || Width < 2) { markers.Clear(); return; }
        if (latest == null) { DrawNotches(e.Graphics); DrawMarkers(e.Graphics); return; }
        int mid = Divider;
        long captureLeft = center - View.SampleRate / 2;
        float X(long hz) => (hz - View.Left) * (Width - 1f) / View.Span;
        float startX = X(captureLeft);
        float dx = (Width - 1f) * View.SampleRate / (View.Span * (float)Bins);
        void DrawTrace(float[] bins, Pen pen)
        {
            var points = Enumerable.Range(0, Bins).Select(i => new PointF(startX + i*dx, -bins[i]/95*Math.Max(1, mid-25))).ToArray();
            e.Graphics.DrawLines(pen, points);
        }
        if (PeakHold && !resetPeaks) DrawTrace(peaks, Pens.Coral);
        DrawTrace(Averaging ? average : latest, Pens.Turquoise);
        int intervals = Math.Clamp(Width / 145, 1, 12);
        for (int tick = 0; tick <= intervals; tick++)
        {
            double fraction = tick / (double)intervals;
            string label = $"{View.FrequencyAt(fraction)/1e6:F3} MHz";
            float width = e.Graphics.MeasureString(label, Font).Width;
            float x = (float)(fraction * (Width - 1));
            e.Graphics.DrawLine(Pens.SlateGray, x, mid - 6, x, mid);
            e.Graphics.DrawString(label, Font, Brushes.Silver, Math.Clamp(x - width / 2, 0, Math.Max(0, Width - width)), mid-25);
        }
        BandPlan.Draw(e.Graphics, Font, View.Left, View.Span, Width, mid - 25 - Font.Height, View.Station);
        float rowHeight = (Height - mid) / (float)history.Height;
        var raster = Raster(Math.Max(1, (int)Math.Round(Bins * dx)));
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        for (int destRow = 0; destRow < count;)
        {
            int sourceY = (firstRow + destRow) % history.Height;
            long rowCenter = rowCenters[sourceY];
            int length = 1;
            while (destRow + length < count && sourceY + length < history.Height &&
                rowCenters[sourceY + length] == rowCenter) length++;
            float x = X(rowCenter - View.SampleRate / 2);
            if (x < Width && x + Bins * dx > 0)
                e.Graphics.DrawImage(raster, new RectangleF(x, mid + destRow * rowHeight, Bins * dx, length * rowHeight),
                new RectangleF(0, sourceY, raster.Width, length), GraphicsUnit.Pixel);
            destRow += length;
        }
        float low = EdgeX(-1), high = EdgeX(1);
        using var shade = new SolidBrush(Color.FromArgb(22, Color.SkyBlue));
        e.Graphics.FillRectangle(shade, low, 0, high - low, Math.Max(1, mid - 25));
        foreach (float edge in new[] {low, high})
        {
            e.Graphics.DrawLine(Pens.SteelBlue, edge, 0, edge, mid - 25);
            e.Graphics.DrawLine(Pens.SteelBlue, edge, mid, edge, Height);
        }
        e.Graphics.FillRectangle(Brushes.SlateGray, 0, mid-2, Width, 4);
        float vfo = X(View.Station);
        e.Graphics.DrawLine(Pens.Gold, vfo, 0, vfo, mid - 24);
        e.Graphics.DrawLine(Pens.Gold, vfo, mid, vfo, Height);
        DrawNotches(e.Graphics);
        DrawMarkers(e.Graphics);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { reduced?.Dispose(); history.Dispose(); }
        base.Dispose(disposing);
    }
}
