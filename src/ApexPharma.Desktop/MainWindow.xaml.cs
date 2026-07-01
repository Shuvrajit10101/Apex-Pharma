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
    private readonly IServiceScopeFactory _scopeFactory;

    public MainWindow(MainViewModel viewModel, IServiceScopeFactory scopeFactory)
    {
        _viewModel = viewModel;
        _scopeFactory = scopeFactory;
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
    /// <remarks>
    /// Each open gets its own DI scope so the <c>MastersWindow</c>, its view-models, and the
    /// four master services share ONE freshly-created scoped <c>ApexPharmaDbContext</c>. The
    /// scope is disposed when the window closes, so the context (and its change tracker) is
    /// released — reopening always reads fresh data and nothing leaks for the app's lifetime.
    /// </remarks>
    private async void ManageMasters_Click(object sender, RoutedEventArgs e)
    {
        IServiceScope scope = _scopeFactory.CreateScope();
        try
        {
            var masters = scope.ServiceProvider.GetRequiredService<MastersWindow>();
            masters.Owner = this;
            // Tie the scope's lifetime to the window: dispose it once the window closes.
            masters.Closed += (_, _) => scope.Dispose();
            await masters.InitializeAsync(_viewModel.CurrentRole);
            masters.Show();
        }
        catch
        {
            // If we never reach Show() (e.g. InitializeAsync throws), dispose the scope
            // here so it is not leaked — Closed will never fire for an unshown window.
            scope.Dispose();
            throw;
        }
    }
}
