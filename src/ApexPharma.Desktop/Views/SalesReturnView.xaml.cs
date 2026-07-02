using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Code-behind for <see cref="SalesReturnView"/> (plan.md §10). View-only: all behaviour lives
/// in the bound <see cref="ViewModels.Returns.SalesReturnViewModel"/> (MVVM — plan.md §8).
/// </summary>
public partial class SalesReturnView : UserControl
{
    public SalesReturnView() => InitializeComponent();
}
