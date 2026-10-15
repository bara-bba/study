using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Study;

/// <summary>Looks up a theme brush by its resource key (e.g. "Brush.Ok").</summary>
internal sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Visible, false → Collapsed. Pass "invert" to flip.</summary>
internal sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "invert")
        {
            b = !b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Empty/null string → Collapsed.</summary>
internal sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
