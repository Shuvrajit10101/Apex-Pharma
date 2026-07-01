using System;
using System.ComponentModel;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Desktop.ViewModels.Masters;
using ApexPharma.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPharma.Desktop.Navigation;

/// <summary>
/// Concrete navigation for the single-window shell (plan.md §10). Owns both module
/// switching and DbContext lifetime: every <see cref="NavigateToAsync"/> creates a fresh
/// DI scope, resolves the target view-model (with its scoped services and
/// <c>ApexPharmaDbContext</c>) from that scope, activates it, sets it as
/// <see cref="CurrentViewModel"/>, then disposes the PREVIOUS module's scope. So each
/// module visit reads fresh data and no context (or its change tracker) leaks — this is
/// the same per-activation scoping the Masters window used, now generalised to the shell.
/// The active scope is disposed on <see cref="Dispose"/> (app shutdown).
/// </summary>
public sealed class NavigationService : INavigationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuthService _auth;

    // How a module maps to a view-model from a scoped provider. Defaults to the static
    // production mapping; an internal constructor lets tests inject a controllable resolver
    // (e.g. a slow or throwing activation) to exercise the re-entrancy and failure paths
    // without touching the scoping/DbContext-lifetime design.
    private readonly Func<IServiceProvider, NavigationModule, object> _resolve;

    private IServiceScope? _currentScope;
    private object? _currentViewModel;
    private NavigationModule _currentModule = NavigationModule.Landing;
    private UserRole _role;
    private bool _disposed;

    // Monotonic token identifying the latest requested navigation. Each NavigateToAsync
    // captures its own token before awaiting activation; when it resumes it checks whether a
    // newer navigation has since started (token advanced) and, if so, stands down — last
    // click wins. Guards the async gap where a slow DB-backed activation could otherwise let
    // an earlier navigation swap in after a later one already landed.
    private long _navigationToken;

    public NavigationService(IServiceScopeFactory scopeFactory, IAuthService auth)
        : this(scopeFactory, auth, Resolve)
    {
    }

    /// <summary>
    /// Test seam: same as the public constructor but with an injectable module→view-model
    /// resolver so tests can drive controllable (slow/throwing) activations. Production code
    /// always uses the public constructor, which supplies the static <see cref="Resolve"/>.
    /// </summary>
    internal NavigationService(
        IServiceScopeFactory scopeFactory,
        IAuthService auth,
        Func<IServiceProvider, NavigationModule, object> resolve)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public object? CurrentViewModel
    {
        get => _currentViewModel;
        private set
        {
            if (!ReferenceEquals(_currentViewModel, value))
            {
                _currentViewModel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentViewModel)));
            }
        }
    }

    public NavigationModule CurrentModule
    {
        get => _currentModule;
        private set
        {
            if (_currentModule != value)
            {
                _currentModule = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentModule)));
            }
        }
    }

    public void SetRole(UserRole role) => _role = role;

    public bool CanNavigateTo(NavigationModule module)
    {
        Permission? required = RequiredPermission(module);
        return required is null || _auth.HasPermission(_role, required.Value);
    }

    public async Task<bool> NavigateToAsync(NavigationModule module)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NavigationService));
        }

        // Refuse navigation the acting role may not perform (plan.md §4). The nav item is
        // also hidden in the shell, but this is the actual boundary, not just cosmetics.
        if (!CanNavigateTo(module))
        {
            return false;
        }

        // Claim this navigation. A later NavigateToAsync will advance the token; when we
        // resume after the await we compare against the current token to see if we were
        // superseded (re-entrancy guard — last click wins).
        long token = ++_navigationToken;

        // Resolve the target from a brand-new scope so its DbContext is fresh for this
        // visit. Build fully before swapping so a failure leaves the current view intact.
        IServiceScope newScope = _scopeFactory.CreateScope();
        object viewModel;
        try
        {
            viewModel = _resolve(newScope.ServiceProvider, module);

            if (viewModel is IActivatableViewModel activatable)
            {
                await activatable.ActivateAsync(_role);
            }
        }
        catch
        {
            // Activation failed: discard the half-built scope and surface failure to the
            // caller. We do NOT rethrow — a DB error activating a module must not crash the
            // app; the caller keeps the current view and reports the error (see
            // MainViewModel). The current scope/view is untouched because we never swapped.
            newScope.Dispose();
            return false;
        }

        // If a newer navigation started while we were awaiting activation — or the service
        // was disposed (Dispose advances the token) — that one wins: discard the scope we
        // just built (disposing it exactly once — no leak, no double dispose) and leave the
        // current view alone. Do NOT swap.
        if (token != _navigationToken)
        {
            newScope.Dispose();
            return false;
        }

        // Swap in the new module, then dispose the PREVIOUS scope so its DbContext (and
        // change tracker) is released. Order matters: the old view-model is detached first.
        IServiceScope? previousScope = _currentScope;
        _currentScope = newScope;
        CurrentModule = module;
        CurrentViewModel = viewModel;
        previousScope?.Dispose();

        return true;
    }

    /// <summary>Maps a module to the permission required to reach it, or null if open to all.</summary>
    private static Permission? RequiredPermission(NavigationModule module) => module switch
    {
        NavigationModule.Masters => Permission.ManageProducts,
        _ => null
    };

    /// <summary>Resolves the view-model for a module from the given (scoped) provider.</summary>
    private static object Resolve(IServiceProvider provider, NavigationModule module) => module switch
    {
        NavigationModule.Landing => provider.GetRequiredService<LandingViewModel>(),
        NavigationModule.Masters => provider.GetRequiredService<MastersViewModel>(),
        _ => BuildPlaceholder(provider, module)
    };

    private static PlaceholderViewModel BuildPlaceholder(IServiceProvider provider, NavigationModule module)
    {
        var vm = provider.GetRequiredService<PlaceholderViewModel>();
        vm.ModuleName = ModuleLabel(module);
        return vm;
    }

    /// <summary>Human-readable label used for placeholder headings and nav highlighting.</summary>
    public static string ModuleLabel(NavigationModule module) => module switch
    {
        NavigationModule.Landing => "Home",
        NavigationModule.Masters => "Masters",
        NavigationModule.Billing => "Billing",
        NavigationModule.Inventory => "Inventory",
        NavigationModule.Purchases => "Purchases",
        NavigationModule.Reports => "Reports",
        NavigationModule.Settings => "Settings",
        _ => module.ToString()
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Supersede any in-flight navigation so that, when it resumes past its await, it
        // stands down and disposes only its own freshly-built scope instead of swapping into
        // (and re-disposing) the scope we release here.
        _navigationToken++;

        _currentScope?.Dispose();
        _currentScope = null;
    }
}
