using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels;

/// <summary>
/// View-model for the shell window. Carries the signed-in user and role so the shell
/// can greet them and (in later phases) show/hide modules by permission. Module
/// navigation is added in Phase 1 (plan.md §10).
/// </summary>
public class MainViewModel : ViewModelBase
{
    private string _title = "Apex-Pharma — Pharmacy Management";
    private string _signedInAs = string.Empty;

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

    /// <summary>Binds the shell to the user returned by a successful login.</summary>
    public void SetUser(User user, UserRole role)
    {
        CurrentUser = user;
        CurrentRole = role;
        string display = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;
        SignedInAs = $"Signed in as {display} ({role})";
    }
}
