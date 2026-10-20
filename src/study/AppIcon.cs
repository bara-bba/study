using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using DColor = System.Drawing.Color;

namespace Study;

/// <summary>
/// Draws the app glyph — an accent rounded square with a pulse/heartbeat trace (monitoring) —
/// at any size. Same construction as Picky's icon so the two apps sit together in the tray.
/// When <c>alarm</c> is true the square is drawn red, so the tray icon itself shows an alarm.
/// </summary>
internal static class AppIcon
{
    public static readonly DColor AlarmRed = DColor.FromArgb(0xC4, 0x2B, 0x1C);

    // Covers the taskbar/title-bar sizes at every common scale (100 % → 24/16, 125 % → 30/20,
    // 150 % → 36/24, 175 % → 42/28, 200 % → 48/32) so Windows never upscales a small frame.
    private static readonly int[] IcoSizes = { 16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 56, 64, 96, 128, 256 };

    // Inset per side: 5.5 % looked small next to Kiro, 1.5 % too big, 3.5 % still a touch big.
    private const float PadFraction = 0.045f;

    // Pulse trace (fractions of the icon size): flat → spike up → dip → flat, scaled ~80 %
    // around the centre so the glyph sits smaller inside the square.
    private static readonly (float X, float Y)[] Trace =
    {
        (0.26f, 0.54f), (0.404f, 0.54f), (0.476f, 0.308f),
        (0.556f, 0.692f), (0.612f, 0.54f), (0.74f, 0.54f),
    };

    private const float StrokeFraction = 0.065f;

    /// <summary>
    /// Vector version of the glyph for in-window use (title bar). Scaling the 256 px bitmap down
    /// to ~22 DIP made it look pixelated; a DrawingImage is rasterised crisply at the real DPI.
    /// </summary>
    public static System.Windows.Media.DrawingImage CreateVectorImage(DColor accent, DColor glyph)
    {
        const double s = 100;
        double pad = s * PadFraction;
        double radius = s * 0.22;

        static System.Windows.Media.SolidColorBrush Brush(DColor c)
        {
            var b = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        var group = new System.Windows.Media.DrawingGroup();

        // Transparent full-size frame so the padding is kept when the Image stretches the drawing.
        group.Children.Add(new System.Windows.Media.GeometryDrawing(
            System.Windows.Media.Brushes.Transparent, null,
            new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, s, s))));

        group.Children.Add(new System.Windows.Media.GeometryDrawing(
            Brush(accent), null,
            new System.Windows.Media.RectangleGeometry(
                new System.Windows.Rect(pad, pad, s - 2 * pad, s - 2 * pad), radius, radius)));

        var trace = new System.Windows.Media.PathFigure
        {
            StartPoint = new System.Windows.Point(s * Trace[0].X, s * Trace[0].Y),
        };
        foreach (var (x, y) in Trace.Skip(1))
        {
            trace.Segments.Add(new System.Windows.Media.LineSegment(
                new System.Windows.Point(s * x, s * y), isStroked: true));
        }

        var pen = new System.Windows.Media.Pen(Brush(glyph), s * StrokeFraction)
        {
            StartLineCap = System.Windows.Media.PenLineCap.Round,
            EndLineCap = System.Windows.Media.PenLineCap.Round,
            LineJoin = System.Windows.Media.PenLineJoin.Round,
        };
        pen.Freeze();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(
            null, pen, new System.Windows.Media.PathGeometry(new[] { trace })));

        var image = new System.Windows.Media.DrawingImage(group);
        image.Freeze();
        return image;
    }

    public static Bitmap Render(int size, DColor accent, DColor glyph)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(DColor.Transparent);

        float s = size;
        float pad = Math.Max(0.5f, s * PadFraction);
        float radius = s * 0.22f;

        using (var bg = new SolidBrush(accent))
        using (var square = RoundedRect(pad, pad, s - 2 * pad, s - 2 * pad, radius))
        {
            g.FillPath(bg, square);
        }

        float thickness = Math.Max(1.3f, s * StrokeFraction);
        using var pen = new Pen(glyph, thickness)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        // Flat → spike up → dip → flat: a single heartbeat across the middle.
        g.DrawLines(pen, Trace.Select(p => new PointF(s * p.X, s * p.Y)).ToArray());

        return bmp;
    }

    public static Icon CreateIcon(DColor accent, DColor glyph)
    {
        using var ms = new MemoryStream(BuildIco(accent, glyph));
        using var raw = new Icon(ms);
        return (Icon)raw.Clone();
    }

    /// <summary>
    /// Window.Icon source. Decoded from the full multi-frame .ico so WPF hands Windows the exact
    /// frame each surface asks for (taskbar, Alt+Tab, title bar) instead of scaling one bitmap.
    /// </summary>
    public static BitmapSource CreateImageSource(DColor accent, DColor glyph)
    {
        var ms = new MemoryStream(BuildIco(accent, glyph));
        var decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        frame.Freeze();
        return frame;
    }

    public static byte[] BuildIco(DColor accent, DColor glyph)
    {
        var frames = new List<(int size, byte[] png)>();
        foreach (var size in IcoSizes)
        {
            using var bmp = Render(size, accent, glyph);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            frames.Add((size, ms.ToArray()));
        }

        using var outMs = new MemoryStream();
        using var w = new BinaryWriter(outMs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)frames.Count);

        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
        {
            w.Write(png);
        }

        w.Flush();
        return outMs.ToArray();
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
