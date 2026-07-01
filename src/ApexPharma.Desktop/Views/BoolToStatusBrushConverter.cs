using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Maps a status <see cref="bool"/> (IsError) to a text brush: red for errors, green
/// otherwise. Keeps the success/failure colour logic out of every view (plan.md §10
/// colour-coded warnings).
/// </summary>
public sealed class BoolToStatusBrushConverter : IValueConverter
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x2D, 0x20));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x7F, 0x37));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ErrorBrush : OkBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
