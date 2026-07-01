using System.Windows;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop;

/// <summary>
/// The application shell window (plan.md §10). A single window whose persistent left nav
/// swaps the content region in place: the <c>ContentControl</c> binds to
/// <see cref="MainViewModel.CurrentViewModel"/>, which the navigation service drives.
/// Resolved from DI and bound to a <see cref="MainViewModel"/>; the authenticated user is
/// applied via <see cref="SetUser"/> right after a successful login.
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

    /// <summary>
    /// Applies the signed-in user + role to the shell (called after login). This also
    /// primes the navigation service with the acting role and shows the landing view.
    /// </summary>
    public void SetUser(User user, UserRole role) => _viewModel.SetUser(user, role);
}
