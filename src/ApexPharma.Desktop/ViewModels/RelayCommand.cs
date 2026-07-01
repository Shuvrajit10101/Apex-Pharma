using System;
using System.Windows.Input;

namespace ApexPharma.Desktop.ViewModels;

/// <summary>
/// A minimal <see cref="ICommand"/> for MVVM (plan.md §8 — presentation layer). Wraps
/// an action and an optional can-execute predicate so view-models expose commands to
/// the view without code-behind. Supports async work by accepting an <c>async void</c>
/// handler is discouraged; callers pass a synchronous delegate that kicks off the work.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>Re-queries <see cref="CanExecute"/> (call when gating state changes).</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
