using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Converts a string to <see cref="Visibility"/>: <see cref="Visibility.Visible"/> when the
/// string has content, <see cref="Visibility.Collapsed"/> when it is null or whitespace.
/// Drives the shell's non-fatal status/error banner so it occupies no space until there is
/// something to show (plan.md §10).
/// </summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
