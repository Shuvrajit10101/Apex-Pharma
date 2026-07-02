using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Maps a <see cref="bool"/> to <see cref="Visibility"/> inverted: <c>false</c> → Visible,
/// <c>true</c> → Collapsed. Used to show one of two mutually-exclusive input panels (e.g. the
/// delta field vs the counted-quantity field on the Stock Adjustment screen) from a single flag.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
