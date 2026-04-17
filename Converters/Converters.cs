using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

// Explicit aliases — WPF + WinForms are both referenced
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;

namespace Pulse.Converters;

/// Passes a SolidColorBrush through (no-op, used as fallback)
public class TileColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is WpfBrush b ? b : new WpfBrush(Colors.White);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// Converts 0..1 fraction + available width → pixel width for progress bar fill
public class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2
            && values[0] is double fraction
            && values[1] is double totalWidth)
        {
            return Math.Max(0, fraction * totalWidth);
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// true → 1.0 opacity, false → 0.4 opacity (dim disabled tiles)
public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? 1.0 : 0.4;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// Converts 0..1 fraction + container width → pixel bar width (same as PercentToWidthConverter — alias)
public class BarWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2
            && values[0] is double fraction
            && values[1] is double width)
        {
            return Math.Max(2, fraction * width);
        }
        return 2.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// Converts a SolidColorBrush → LinearGradientBrush for bar fill (semi-transparent start)
public class BarColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is WpfBrush b)
        {
            var c = b.Color;
            return new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(WpfColor.FromArgb(180, c.R, c.G, c.B), 0),
                    new GradientStop(c, 1)
                },
                new System.Windows.Point(0, 0),
                new System.Windows.Point(1, 0));
        }
        return new WpfBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
