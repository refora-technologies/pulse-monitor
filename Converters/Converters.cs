using System.Globalization;
using System.Windows.Data;

namespace Pulse.Converters;

/// <summary>
/// Inverts a boolean, and declines to answer for anything else.
/// </summary>
/// <remarks>
/// It used to return false for a non-boolean, which includes
/// <see cref="System.Windows.DependencyProperty.UnsetValue"/> — the value WPF passes while a binding is still
/// being set up. So the inverse of "not known yet" was "false", and the one place this is used
/// binds IsEnabled: the button was briefly disabled on its way to being enabled, for no reason
/// anybody had asked for.
///
/// UnsetValue is how a converter says it has no opinion, and it leaves the target at its own
/// declared default, which is the right answer for a value that has not arrived.
/// </remarks>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : System.Windows.DependencyProperty.UnsetValue;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : System.Windows.DependencyProperty.UnsetValue;
}

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
