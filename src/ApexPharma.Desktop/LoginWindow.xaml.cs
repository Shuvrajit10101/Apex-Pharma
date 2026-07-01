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
            Close();
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }
}
