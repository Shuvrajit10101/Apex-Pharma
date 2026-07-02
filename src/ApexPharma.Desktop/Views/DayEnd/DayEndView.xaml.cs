using System.Windows.Controls;
using ApexPharma.Desktop.ViewModels.DayEnd;

namespace ApexPharma.Desktop.Views.DayEnd;

/// <summary>
/// Code-behind for <see cref="DayEndView"/> (plan.md §3, §10, §11 — Phase 2e). View-only except for a
/// small validation bridge: it counts WPF binding <c>Validation.Error</c> events on the money
/// TextBoxes and flips <see cref="DayEndViewModel.HasInputError"/> so the close command's CanExecute
/// blocks a finalize while any amount is cleared/invalid. All money rules still live in the service
/// (MVVM — plan.md §8); this only surfaces input validity to the VM.
/// </summary>
public partial class DayEndView : UserControl
{
    private int _errorCount;

    public DayEndView()
    {
        InitializeComponent();
        Validation.AddErrorHandler(this, OnValidationError);
    }

    private void OnValidationError(object? sender, ValidationErrorEventArgs e)
    {
        if (e.Action == ValidationErrorEventAction.Added)
        {
            _errorCount++;
        }
        else if (e.Action == ValidationErrorEventAction.Removed && _errorCount > 0)
        {
            _errorCount--;
        }

        if (DataContext is DayEndViewModel vm)
        {
            vm.HasInputError = _errorCount > 0;
        }
    }
}
