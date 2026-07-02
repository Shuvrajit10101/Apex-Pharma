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
    private readonly ISessionContext _session;

    private string _title = "Apex-Pharma — Pharmacy Management";
    private string _signedInAs = string.Empty;
    private bool _canManageMasters;
    private bool _canDoPurchases;
    private bool _canViewStock;
    private bool _canAdjustStock;
    private bool _canDoBilling;
    private bool _canManageSettings;
    private bool _canViewReports;
    private string? _statusMessage;

    public MainViewModel(IAuthService auth, INavigationService navigation, ISessionContext session)
    {
        _auth = auth;
        _navigation = navigation;
        _session = session;

        // Re-raise the shell's bindings when the navigation service swaps content or the
        // active module changes (drives the ContentControl and active-nav highlighting).
        _navigation.PropertyChanged += OnNavigationChanged;

        NavigateBillingCommand = new RelayCommand(() => Navigate(NavigationModule.Billing));
        NavigateInventoryCommand = new RelayCommand(() => Navigate(NavigationModule.Inventory));
        NavigateStockAdjustmentsCommand = new RelayCommand(() => Navigate(NavigationModule.StockAdjustments));
        NavigatePurchasesCommand = new RelayCommand(() => Navigate(NavigationModule.Purchases));
        NavigateSalesReturnCommand = new RelayCommand(() => Navigate(NavigationModule.SalesReturn));
        NavigatePurchaseReturnCommand = new RelayCommand(() => Navigate(NavigationModule.PurchaseReturn));
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

    /// <summary>
    /// True when the signed-in role may record purchases (<see cref="Permission.DoPurchases"/>).
    /// Owner + Pharmacist, not Cashier — drives the Purchases nav button's visibility (plan.md §4).
    /// </summary>
    public bool CanDoPurchases
    {
        get => _canDoPurchases;
        private set => SetProperty(ref _canDoPurchases, value);
    }

    /// <summary>
    /// True when the signed-in role may view stock (<see cref="Permission.ViewStock"/>) —
    /// all roles today. Drives the Inventory nav button's visibility (plan.md §4).
    /// </summary>
    public bool CanViewStock
    {
        get => _canViewStock;
        private set => SetProperty(ref _canViewStock, value);
    }

    /// <summary>
    /// True when the signed-in role may adjust stock (<see cref="Permission.AdjustStock"/>) —
    /// Owner + Pharmacist, not Cashier. Drives the Stock Adjustments nav button's visibility (plan.md §4).
    /// </summary>
    public bool CanAdjustStock
    {
        get => _canAdjustStock;
        private set => SetProperty(ref _canAdjustStock, value);
    }

    /// <summary>
    /// True when the signed-in role may operate the POS (<see cref="Permission.DoBilling"/>) —
    /// Owner + Pharmacist + Cashier. Drives the Billing nav button's visibility (plan.md §4).
    /// </summary>
    public bool CanDoBilling
    {
        get => _canDoBilling;
        private set => SetProperty(ref _canDoBilling, value);
    }

    /// <summary>
    /// True when the signed-in role may change pharmacy settings
    /// (<see cref="Permission.ManageSettings"/>) — Owner only. Drives the Settings nav button's
    /// visibility so only the Owner sees GSTIN/DL/tax configuration (plan.md §4).
    /// </summary>
    public bool CanManageSettings
    {
        get => _canManageSettings;
        private set => SetProperty(ref _canManageSettings, value);
    }

    /// <summary>
    /// True when the signed-in role may view the report suite (<see cref="Permission.ViewReports"/>)
    /// — Owner + Pharmacist, not Cashier. Drives the Reports nav button's visibility (plan.md §4, §11).
    /// </summary>
    public bool CanViewReports
    {
        get => _canViewReports;
        private set => SetProperty(ref _canViewReports, value);
    }

    /// <summary>
    /// Transient status/error banner text for the shell (null/empty = hidden). Used to
    /// surface a non-fatal navigation failure — e.g. a module's data load hit a DB error —
    /// so the counter app reports the problem instead of crashing (plan.md §10).
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>The view-model hosted in the content region (bound to a ContentControl).</summary>
    public object? CurrentViewModel => _navigation.CurrentViewModel;

    /// <summary>The active module — used by nav-item style triggers to highlight the selection.</summary>
    public NavigationModule CurrentModule => _navigation.CurrentModule;

    public ICommand NavigateBillingCommand { get; }
    public ICommand NavigateInventoryCommand { get; }

    /// <summary>Opens the Stock Adjustments module (gated on <see cref="Permission.AdjustStock"/>, plan.md §4).</summary>
    public ICommand NavigateStockAdjustmentsCommand { get; }

    public ICommand NavigatePurchasesCommand { get; }

    /// <summary>Opens the Sales-return flow (gated on <see cref="Permission.DoBilling"/>, plan.md §4).</summary>
    public ICommand NavigateSalesReturnCommand { get; }

    /// <summary>Opens the Purchase-return flow (gated on <see cref="Permission.DoPurchases"/>, plan.md §4).</summary>
    public ICommand NavigatePurchaseReturnCommand { get; }

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
        CanDoPurchases = _auth.HasPermission(role, Permission.DoPurchases);
        CanViewStock = _auth.HasPermission(role, Permission.ViewStock);
        CanAdjustStock = _auth.HasPermission(role, Permission.AdjustStock);
        CanDoBilling = _auth.HasPermission(role, Permission.DoBilling);
        CanManageSettings = _auth.HasPermission(role, Permission.ManageSettings);
        CanViewReports = _auth.HasPermission(role, Permission.ViewReports);

        // Prime the session so per-visit module view-models can attribute their mutations
        // (e.g. a Purchase's CreatedBy) to the acting user (plan.md §4).
        _session.SetUser(user.UserId, role);

        _navigation.SetRole(role);
        await NavigateSafeAsync(NavigationModule.Landing);
    }

    private async void Navigate(NavigationModule module) => await NavigateSafeAsync(module);

    /// <summary>
    /// Runs a navigation without ever letting a failure escape this <c>async void</c> entry
    /// point (which would tear down the app). A module's <c>ActivateAsync</c> hitting a DB
    /// error now surfaces as a status banner and leaves the current view intact rather than
    /// crashing the counter app (plan.md §10).
    /// </summary>
    private async Task NavigateSafeAsync(NavigationModule module)
    {
        try
        {
            bool navigated = await _navigation.NavigateToAsync(module);

            // A refusal by permission is silent (the nav item is hidden anyway); only an
            // attempted-but-failed activation is worth reporting. NavigateToAsync returns
            // false for both, so we distinguish by whether the role may reach the module.
            if (!navigated && _navigation.CanNavigateTo(module))
            {
                StatusMessage = $"Couldn't open {NavigationService.ModuleLabel(module)}. Please try again.";
            }
            else
            {
                StatusMessage = null;
            }
        }
        catch (Exception ex)
        {
            // Belt-and-braces: NavigateToAsync is designed not to throw on activation
            // failure, but if anything unexpected escapes we still must not crash from an
            // async void. Keep the current view and tell the user.
            StatusMessage = $"Couldn't open {NavigationService.ModuleLabel(module)}: {ex.Message}";
        }
    }

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
