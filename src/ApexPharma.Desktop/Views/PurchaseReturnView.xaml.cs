using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Code-behind for <see cref="PurchaseReturnView"/> (plan.md §10). View-only: all behaviour
/// lives in the bound <see cref="ViewModels.Returns.PurchaseReturnViewModel"/> (MVVM — plan.md §8).
/// </summary>
public partial class PurchaseReturnView : UserControl
{
    public PurchaseReturnView() => InitializeComponent();
}
