using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ApexPharma.Application.Services;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Colour-codes an inventory <see cref="StockRow"/> for the read-only stock grid
/// (plan.md §10 colour-coded warnings): expired = red, near-expiry = amber, low-stock =
/// blue tint, otherwise transparent. Expiry outranks low-stock because an expired lot is
/// the more urgent signal. Keeps the highlight logic out of the view.
/// </summary>
public sealed class StockRowBrushConverter : IValueConverter
{
    private static readonly Brush Expired = new SolidColorBrush(Color.FromRgb(0xFD, 0xE2, 0xE1)); // light red
    private static readonly Brush NearExpiry = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xCD)); // amber
    private static readonly Brush LowStock = new SolidColorBrush(Color.FromRgb(0xDD, 0xEB, 0xFF)); // blue tint
    private static readonly Brush None = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not StockRow row)
        {
            return None;
        }

        if (row.IsExpired)
        {
            return Expired;
        }

        if (row.IsNearExpiry)
        {
            return NearExpiry;
        }

        return row.IsLowStock ? LowStock : None;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
