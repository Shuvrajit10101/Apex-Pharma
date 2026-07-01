using ApexPharma.Application.Services;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels;

/// <summary>
/// View-model for the shell window. Carries the signed-in user and role so the shell
/// can greet them and show/hide modules by permission. The Masters area is gated on
/// <see cref="Permission.ManageProducts"/> (plan.md §4, §10).
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly IAuthService _auth;

    private string _title = "Apex-Pharma — Pharmacy Management";
    private string _signedInAs = string.Empty;
    private bool _canManageMasters;

    public MainViewModel(IAuthService auth) => _auth = auth;

    /// <summary>Window title shown in the title bar (bound in MainWindow.xaml).</summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>The authenticated user for this session (null until <see cref="SetUser"/>).</summary>
    public User? CurrentUser { get; private set; }

    /// <summary>The signed-in user's role tier for permission-gated UI.</summary>
    public UserRole CurrentRole { get; private set; }

    /// <summary>"Signed in as {name} ({role})" banner text.</summary>
    public string SignedInAs
    {
        get => _signedInAs;
        private set => SetProperty(ref _signedInAs, value);
    }

    /// <summary>
    /// True when the signed-in role may open the Masters area (has
    /// <see cref="Permission.ManageProducts"/>). Drives the nav button's visibility so a
    /// Cashier never sees master-data management (plan.md §4).
    /// </summary>
    public bool CanManageMasters
    {
        get => _canManageMasters;
        private set => SetProperty(ref _canManageMasters, value);
    }

    /// <summary>Binds the shell to the user returned by a successful login.</summary>
    public void SetUser(User user, UserRole role)
    {
        CurrentUser = user;
        CurrentRole = role;
        string display = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;
        SignedInAs = $"Signed in as {display} ({role})";
        CanManageMasters = _auth.HasPermission(role, Permission.ManageProducts);
    }
}
