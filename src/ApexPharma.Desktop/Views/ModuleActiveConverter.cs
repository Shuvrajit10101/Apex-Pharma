using System;
using System.Globalization;
using System.Windows.Data;
using ApexPharma.Desktop.Navigation;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Multi-value converter that returns <c>true</c> when a nav button's target module (its
/// <c>Tag</c>) equals the shell's current module. Drives the active-nav-item highlight in
/// MainWindow (plan.md §10). Inputs: [0] the button's <see cref="NavigationModule"/> tag,
/// [1] the shell's current <see cref="NavigationModule"/>.
/// </summary>
public sealed class ModuleActiveConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is { Length: 2 } &&
            values[0] is NavigationModule tag &&
            values[1] is NavigationModule current)
        {
            return tag == current;
        }

        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
