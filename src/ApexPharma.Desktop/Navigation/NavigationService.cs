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

    private IServiceScope? _currentScope;
    private object? _currentViewModel;
    private NavigationModule _currentModule = NavigationModule.Landing;
    private UserRole _role;
    private bool _disposed;

    public NavigationService(IServiceScopeFactory scopeFactory, IAuthService auth)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
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

        // Resolve the target from a brand-new scope so its DbContext is fresh for this
        // visit. Build fully before swapping so a failure leaves the current view intact.
        IServiceScope newScope = _scopeFactory.CreateScope();
        object viewModel;
        try
        {
            viewModel = Resolve(newScope.ServiceProvider, module);

            if (viewModel is IActivatableViewModel activatable)
            {
                await activatable.ActivateAsync(_role);
            }
        }
        catch
        {
            // Activation failed: discard the half-built scope, keep the current view.
            newScope.Dispose();
            throw;
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
        _currentScope?.Dispose();
        _currentScope = null;
    }
}
