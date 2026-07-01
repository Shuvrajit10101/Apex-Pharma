namespace ApexPharma.Desktop.ViewModels;

/// <summary>
/// A reusable view-model for modules not yet built (Billing, Inventory, Purchases,
/// Reports, Settings). It shows a clear "‹Module› — coming in a later phase" panel so
/// every nav item gives feedback and no button is dead (plan.md §10). The module label
/// is set by the navigation service when it resolves the placeholder for a given module.
/// </summary>
public class PlaceholderViewModel : ViewModelBase
{
    private string _moduleName = "Module";

    /// <summary>The human-readable module label (e.g. "Billing"), set on navigation.</summary>
    public string ModuleName
    {
        get => _moduleName;
        set
        {
            if (SetProperty(ref _moduleName, value))
            {
                OnPropertyChanged(nameof(Headline));
            }
        }
    }

    /// <summary>The message rendered in the placeholder panel.</summary>
    public string Headline => $"{ModuleName} — coming in a later phase";
}
