using System.Windows;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop;

/// <summary>
/// The application shell window. Resolved from DI and bound to a
/// <see cref="MainViewModel"/>; the authenticated user is applied via
/// <see cref="SetUser"/> right after a successful login. Navigation buttons are
/// placeholders for now; module views are built in later Phase 1 steps (plan.md §10).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>Applies the signed-in user + role to the shell (called after login).</summary>
    public void SetUser(User user, UserRole role) => _viewModel.SetUser(user, role);
}
