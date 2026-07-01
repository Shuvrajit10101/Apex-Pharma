using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ApexPharma.Desktop.ViewModels;

/// <summary>
/// Base class for MVVM view-models (plan.md §8 — presentation layer). Provides
/// <see cref="INotifyPropertyChanged"/> and a <see cref="SetProperty{T}"/> helper so
/// view-models raise change notifications without boilerplate.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Sets <paramref name="field"/> to <paramref name="value"/> and raises
    /// <see cref="PropertyChanged"/> only if the value actually changed.
    /// </summary>
    /// <returns><c>true</c> if the value changed; otherwise <c>false</c>.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
