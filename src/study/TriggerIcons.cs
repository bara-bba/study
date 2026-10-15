using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Study.Core.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Study;

/// <summary>
/// Small vector icons for the trigger modes, drawn on a 16×16 grid so they stay sharp at any DPI:
/// rising/falling edge as a step waveform with an arrow, TRUE/FALSE as a boxed 1/0, expression as {x}.
/// </summary>
internal static class TriggerIcons
{
    private const double Size = 16;
    private static readonly Dictionary<TriggerMode, DrawingImage> Cache = new();

    public static ImageSource For(TriggerMode mode)
    {
        if (!Cache.TryGetValue(mode, out var image))
        {
            image = Build(mode);
            Cache[mode] = image;
        }

        return image;
    }

    private static DrawingImage Build(TriggerMode mode)
    {
        var brush = Application.Current?.TryFindResource("Brush.TextSecondary") as Brush ?? Brushes.Gainsboro;
        var pen = new Pen(brush, 1.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var group = new DrawingGroup();
        // Transparent frame keeps every icon the same 16×16 box, so they line up in a column.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Size, Size))));

        switch (mode)
        {
            case TriggerMode.RisingEdge:
                group.Children.Add(Stroke(pen, "M 1,13 H 7 V 3 H 15"));
                group.Children.Add(Stroke(pen, "M 4.5,9.5 L 7,7 L 9.5,9.5"));
                break;
            case TriggerMode.FallingEdge:
                group.Children.Add(Stroke(pen, "M 1,3 H 7 V 13 H 15"));
                group.Children.Add(Stroke(pen, "M 4.5,6.5 L 7,9 L 9.5,6.5"));
                break;
            case TriggerMode.WhileTrue:
                group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(1.5, 1.5, 13, 13), 3, 3)));
                group.Children.Add(Text(brush, "1", 11, FontWeights.SemiBold));
                break;
            case TriggerMode.WhileFalse:
                group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(1.5, 1.5, 13, 13), 3, 3)));
                group.Children.Add(Text(brush, "0", 11, FontWeights.SemiBold));
                break;
            default:
                group.Children.Add(Text(brush, "{x}", 11.5, FontWeights.SemiBold));
                break;
        }

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static GeometryDrawing Stroke(Pen pen, string data) => new(null, pen, Geometry.Parse(data));

    /// <summary>Text as geometry, centred on the 16×16 grid by its real ink bounds (not the line box).</summary>
    private static GeometryDrawing Text(Brush brush, string text, double size, FontWeight weight)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, 1.0);
        var geometry = ft.BuildGeometry(new Point(0, 0));
        var b = geometry.Bounds;
        geometry.Transform = new TranslateTransform((Size - b.Width) / 2 - b.X, (Size - b.Height) / 2 - b.Y);
        return new GeometryDrawing(brush, null, geometry);
    }
}

/// <summary>TriggerMode → its 16×16 icon (for card rows and the trigger drop-down).</summary>
internal sealed class TriggerModeToIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TriggerMode m ? TriggerIcons.For(m) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>One entry of the trigger drop-down.</summary>
internal sealed record TriggerOption(TriggerMode Mode, string Text)
{
    public ImageSource Icon => TriggerIcons.For(Mode);
}
