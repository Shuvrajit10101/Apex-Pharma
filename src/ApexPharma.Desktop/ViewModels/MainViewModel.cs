using System.ComponentModel;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels;

/// <summary>
/// View-model for the single-window shell (plan.md §10). Carries the signed-in user and
/// role for the header and permission-gated nav, and drives in-place module switching via
/// the <see cref="INavigationService"/>: each nav command swaps the content region's
/// view-model rather than opening a new window. The Masters item is gated on
/// <see cref="Permission.ManageProducts"/> (plan.md §4, §10).
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly IAuthService _auth;
    private readonly INavigationService _navigation;

    private string _title = "Apex-Pharma — Pharmacy Management";
    private string _signedInAs = string.Empty;
    private bool _canManageMasters;

    public MainViewModel(IAuthService auth, INavigationService navigation)
    {
        _auth = auth;
        _navigation = navigation;

        // Re-raise the shell's bindings when the navigation service swaps content or the
        // active module changes (drives the ContentControl and active-nav highlighting).
        _navigation.PropertyChanged += OnNavigationChanged;

        NavigateBillingCommand = new RelayCommand(() => Navigate(NavigationModule.Billing));
        NavigateInventoryCommand = new RelayCommand(() => Navigate(NavigationModule.Inventory));
        NavigatePurchasesCommand = new RelayCommand(() => Navigate(NavigationModule.Purchases));
        NavigateMastersCommand = new RelayCommand(() => Navigate(NavigationModule.Masters));
        NavigateReportsCommand = new RelayCommand(() => Navigate(NavigationModule.Reports));
        NavigateSettingsCommand = new RelayCommand(() => Navigate(NavigationModule.Settings));
    }

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

    /// <summary>The view-model hosted in the content region (bound to a ContentControl).</summary>
    public object? CurrentViewModel => _navigation.CurrentViewModel;

    /// <summary>The active module — used by nav-item style triggers to highlight the selection.</summary>
    public NavigationModule CurrentModule => _navigation.CurrentModule;

    public ICommand NavigateBillingCommand { get; }
    public ICommand NavigateInventoryCommand { get; }
    public ICommand NavigatePurchasesCommand { get; }
    public ICommand NavigateMastersCommand { get; }
    public ICommand NavigateReportsCommand { get; }
    public ICommand NavigateSettingsCommand { get; }

    /// <summary>
    /// Binds the shell to the user returned by a successful login, primes the navigation
    /// service with the acting role, and shows the default landing view.
    /// </summary>
    public async void SetUser(User user, UserRole role)
    {
        CurrentUser = user;
        CurrentRole = role;
        string display = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;
        SignedInAs = $"Signed in as {display} ({role})";
        CanManageMasters = _auth.HasPermission(role, Permission.ManageProducts);

        _navigation.SetRole(role);
        await _navigation.NavigateToAsync(NavigationModule.Landing);
    }

    private async void Navigate(NavigationModule module) => await _navigation.NavigateToAsync(module);

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(INavigationService.CurrentViewModel))
        {
            OnPropertyChanged(nameof(CurrentViewModel));
        }
        else if (e.PropertyName == nameof(INavigationService.CurrentModule))
        {
            OnPropertyChanged(nameof(CurrentModule));
        }
    }
}
