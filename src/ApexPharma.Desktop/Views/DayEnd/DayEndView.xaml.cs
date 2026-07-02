using System.Windows.Controls;

namespace ApexPharma.Desktop.Views.DayEnd;

/// <summary>
/// Code-behind for <see cref="DayEndView"/> (plan.md §3, §10, §11 — Phase 2e). View-only: all
/// behaviour lives in the bound <see cref="ViewModels.DayEnd.DayEndViewModel"/> (MVVM — plan.md §8).
/// </summary>
public partial class DayEndView : UserControl
{
    public DayEndView() => InitializeComponent();
}
