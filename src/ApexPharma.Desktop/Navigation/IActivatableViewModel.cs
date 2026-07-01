using System.Threading.Tasks;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.Navigation;

/// <summary>
/// Implemented by module view-models that need to load data (or apply the acting role)
/// when they become the active content. The <see cref="INavigationService"/> calls
/// <see cref="ActivateAsync"/> once, right after resolving the view-model from its fresh
/// DI scope — this is where Masters runs its <c>InitializeAsync(role)</c>. View-models
/// with nothing to load (e.g. the placeholder) simply don't implement it.
/// </summary>
public interface IActivatableViewModel
{
    /// <summary>Loads the view-model's data for the signed-in <paramref name="role"/>.</summary>
    Task ActivateAsync(UserRole role);
}
