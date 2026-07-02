using System;
using System.Windows;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPharma.Desktop;

/// <summary>
/// The login screen (plan.md §6.1, §10). Binds to a <see cref="LoginViewModel"/> for
/// the username / error text; the password is read from the <c>PasswordBox</c> at
/// sign-in time (WPF has no secure password binding). On success it opens the
/// login-gated <see cref="MainWindow"/> with the authenticated user and closes itself.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;
    private readonly IServiceProvider _services;

    public LoginWindow(LoginViewModel viewModel, IServiceProvider services)
    {
        _viewModel = viewModel;
        _services = services;
        DataContext = _viewModel;
        InitializeComponent();
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) => await AttemptLoginAsync();

    private async void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AttemptLoginAsync();
        }
    }

    private async System.Threading.Tasks.Task AttemptLoginAsync()
    {
        LoginButton.IsEnabled = false;
        try
        {
            AuthResult? result = await _viewModel.AuthenticateAsync(PasswordBox.Password);
            if (result is null || !result.Succeeded || result.User is null)
            {
                // ErrorMessage is already set on the view-model; stay on the login screen.
                PasswordBox.Clear();
                PasswordBox.Focus();
                return;
            }

            var main = _services.GetRequiredService<MainWindow>();
            main.SetUser(result.User, result.Role);
            System.Windows.Application.Current.MainWindow = main;
            main.Show();

            // Kick off the auto-daily backup in the background (non-blocking). It runs only if
            // enabled, the user may back up (Owner), and no successful backup exists for today —
            // and it swallows its own errors, so a backup problem never blocks or crashes the app
            // (plan.md §13, §6.2).
            TriggerDailyBackup(result.Role);

            Close();
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Fires the auto-daily backup on a background task in its own DI scope (so the scoped
    /// DbContext/services aren't tied to any UI scope). Fire-and-forget: <c>RunDailyIfDueAsync</c>
    /// is non-throwing, so we don't await it and any failure stays contained (plan.md §13).
    /// </summary>
    private void TriggerDailyBackup(ApexPharma.Domain.Enums.UserRole role)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            using var scope = _services.CreateScope();
            var backup = scope.ServiceProvider.GetRequiredService<IBackupService>();
            await backup.RunDailyIfDueAsync(role);
        });
    }
}
