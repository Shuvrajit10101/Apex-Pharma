using System;
using System.Windows;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Desktop.Views;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPharma.Desktop;

/// <summary>
/// The application shell window. Resolved from DI and bound to a
/// <see cref="MainViewModel"/>; the authenticated user is applied via
/// <see cref="SetUser"/> right after a successful login. The Masters button opens the
/// permission-gated master-data area (plan.md §10); other modules land in later steps.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IServiceProvider _services;

    public MainWindow(MainViewModel viewModel, IServiceProvider services)
    {
        _viewModel = viewModel;
        _services = services;
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>Applies the signed-in user + role to the shell (called after login).</summary>
    public void SetUser(User user, UserRole role) => _viewModel.SetUser(user, role);

    /// <summary>
    /// Opens the Masters window for the current role. The button is only visible when the
    /// role has <c>ManageProducts</c>, but the services re-check permission on every
    /// mutation — the UI gate is convenience, not the security boundary (plan.md §4).
    /// </summary>
    private async void ManageMasters_Click(object sender, RoutedEventArgs e)
    {
        var masters = _services.GetRequiredService<MastersWindow>();
        masters.Owner = this;
        await masters.InitializeAsync(_viewModel.CurrentRole);
        masters.Show();
    }
}
