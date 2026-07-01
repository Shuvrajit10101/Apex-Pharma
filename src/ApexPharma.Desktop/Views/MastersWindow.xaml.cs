using System.Windows;
using ApexPharma.Desktop.ViewModels.Masters;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The Master Data window (plan.md §10). Hosts a tab per catalog entity, each bound to
/// its list view-model on the shared <see cref="MastersViewModel"/>. Resolved from DI;
/// the signed-in role is applied via <see cref="InitializeAsync"/> so every tab gates
/// mutations through the services (plan.md §4).
/// </summary>
public partial class MastersWindow : Window
{
    private readonly MastersViewModel _viewModel;

    public MastersWindow(MastersViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>Loads all tabs' data for the signed-in role (call before/after Show).</summary>
    public System.Threading.Tasks.Task InitializeAsync(UserRole actingRole)
        => _viewModel.InitializeAsync(actingRole);
}
