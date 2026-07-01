using System.Threading.Tasks;
using ApexPharma.Application.Services;

namespace ApexPharma.Desktop.ViewModels;

/// <summary>
/// View-model for the login screen (plan.md §6.1, §10). Holds the username and the
/// error text; the password is supplied by the view's <c>PasswordBox</c> at sign-in
/// time (WPF cannot bind a password securely), so it is passed to
/// <see cref="AuthenticateAsync"/> rather than held as a bindable property.
/// </summary>
public class LoginViewModel : ViewModelBase
{
    private readonly IAuthService _auth;

    private string _username = string.Empty;
    private string? _errorMessage;
    private bool _isBusy;

    public LoginViewModel(IAuthService auth) => _auth = auth;

    /// <summary>The entered username (bound to a TextBox).</summary>
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>Generic sign-in error shown in red (null/empty = no error).</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>True while a login attempt is in flight (disables the button).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// Attempts to authenticate with the current <see cref="Username"/> and the
    /// supplied password. Returns the successful <see cref="AuthResult"/> on success,
    /// or <c>null</c> on any failure — in which case <see cref="ErrorMessage"/> holds a
    /// generic, non-revealing message (plan.md §14).
    /// </summary>
    public async Task<AuthResult?> AuthenticateAsync(string password)
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "Enter your username.";
            return null;
        }

        IsBusy = true;
        try
        {
            AuthResult result = await _auth.LoginAsync(Username.Trim(), password);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Error;
                return null;
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
