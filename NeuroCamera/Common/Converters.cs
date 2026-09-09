using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NeuroCamera.Common;

/// <summary>
/// Converts a bound object to <see cref="Visibility"/> based on whether it is null.
/// By default null -> Visible (useful for "no signal" placeholders); pass converter
/// parameter "Invert" to flip it (null -> Collapsed, non-null -> Visible).
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNull = value is null;
        bool invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        bool showPlaceholder = invert ? !isNull : isNull;
        return showPlaceholder ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when the bound string is null/empty/whitespace.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
