using System;
using System.ComponentModel;
using System.Threading.Tasks;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.Navigation;

/// <summary>
/// Owns module switching for the single-window shell (plan.md §10) AND the DbContext
/// lifetime that goes with it. Each navigation creates a fresh DI <b>scope</b>, resolves
/// the target module's view-model (and its scoped services / <c>ApexPharmaDbContext</c>)
/// from that scope, sets it as <see cref="CurrentViewModel"/>, and disposes the PREVIOUS
/// module's scope — so every module visit reads fresh data and nothing leaks. This
/// preserves the per-activation scoping already established for the Masters area.
/// </summary>
public interface INavigationService : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// The view-model currently hosted in the shell's content region. A WPF
    /// <c>ContentControl</c> binds to this and picks the matching view via DataTemplate.
    /// </summary>
    object? CurrentViewModel { get; }

    /// <summary>The module currently displayed (drives active-nav-item highlighting).</summary>
    NavigationModule CurrentModule { get; }

    /// <summary>
    /// Establishes the acting role for permission-gated navigation (plan.md §4). Called
    /// once after login, before any navigation. Navigating to a module the role may not
    /// reach is refused.
    /// </summary>
    void SetRole(UserRole role);

    /// <summary>
    /// Switches the content region to <paramref name="module"/>: creates a new scope,
    /// resolves + activates the target view-model, then disposes the previous scope.
    /// Refused (no change) when the acting role lacks the module's required permission.
    /// </summary>
    /// <returns><c>true</c> if navigation happened; <c>false</c> if it was refused.</returns>
    Task<bool> NavigateToAsync(NavigationModule module);

    /// <summary>True if the acting role may navigate to <paramref name="module"/> (plan.md §4).</summary>
    bool CanNavigateTo(NavigationModule module);
}
