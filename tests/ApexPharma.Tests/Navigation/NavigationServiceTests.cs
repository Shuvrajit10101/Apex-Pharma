using System;
using System.Threading.Tasks;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Desktop.ViewModels.Inventory;
using ApexPharma.Desktop.ViewModels.Masters;
using ApexPharma.Desktop.ViewModels.Purchases;
using ApexPharma.Domain.Enums;
using Xunit;

namespace ApexPharma.Tests.Navigation;

/// <summary>
/// Unit tests for the single-window navigation service (plan.md §10). Cover the
/// testable navigation logic: navigating sets <see cref="INavigationService.CurrentViewModel"/>
/// to the expected view-model type; navigating away disposes the PREVIOUS module's DI
/// scope (per-visit DbContext lifetime); and Masters navigation is refused without
/// <see cref="Permission.ManageProducts"/> but allowed with it (plan.md §4).
/// </summary>
public class NavigationServiceTests : IDisposable
{
    private readonly NavigationTestHost _host;
    private readonly ProbingScopeFactory _scopeFactory;
    private readonly NavigationService _sut;

    public NavigationServiceTests()
    {
        _host = new NavigationTestHost();
        _scopeFactory = _host.CreateProbingScopeFactory();
        _sut = _host.CreateNavigationService(_scopeFactory);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task NavigateToLanding_SetsLandingViewModel()
    {
        _sut.SetRole(UserRole.Owner);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Landing);

        Assert.True(navigated);
        Assert.IsType<LandingViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Landing, _sut.CurrentModule);
    }

    [Fact]
    public async Task NavigateToReports_WithViewReports_SetsReportsViewModel()
    {
        // Owner + Pharmacist have ViewReports (plan.md §4, §11). Reports is a real module (Phase 1f).
        _sut.SetRole(UserRole.Pharmacist);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Reports);

        Assert.True(navigated);
        Assert.IsType<ApexPharma.Desktop.ViewModels.Reports.ReportsViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Reports, _sut.CurrentModule);
    }

    [Fact]
    public async Task NavigateToReports_AsCashier_IsRefused()
    {
        // Cashier lacks ViewReports (plan.md §4); the current view stays put.
        _sut.SetRole(UserRole.Cashier);
        await _sut.NavigateToAsync(NavigationModule.Landing);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Reports);

        Assert.False(navigated);
        Assert.IsType<LandingViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Landing, _sut.CurrentModule);
    }

    [Fact]
    public void CanNavigateTo_Reports_MatchesViewReportsPermission()
    {
        _sut.SetRole(UserRole.Owner);
        Assert.True(_sut.CanNavigateTo(NavigationModule.Reports));

        _sut.SetRole(UserRole.Pharmacist);
        Assert.True(_sut.CanNavigateTo(NavigationModule.Reports)); // Pharmacist has ViewReports

        _sut.SetRole(UserRole.Cashier);
        Assert.False(_sut.CanNavigateTo(NavigationModule.Reports)); // Cashier does not
    }

    [Fact]
    public async Task NavigateToSettings_AsOwner_SetsSettingsViewModel()
    {
        // Settings is Owner-only (ManageSettings — plan.md §4).
        _sut.SetRole(UserRole.Owner);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Settings);

        Assert.True(navigated);
        Assert.IsType<ApexPharma.Desktop.ViewModels.Settings.SettingsViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Settings, _sut.CurrentModule);
    }

    [Fact]
    public async Task NavigateToSettings_AsCashier_IsRefused()
    {
        // Cashier lacks ManageSettings (plan.md §4); the current view stays put.
        _sut.SetRole(UserRole.Cashier);
        await _sut.NavigateToAsync(NavigationModule.Landing);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Settings);

        Assert.False(navigated);
        Assert.IsType<LandingViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Landing, _sut.CurrentModule);
    }

    [Fact]
    public async Task NavigateToSettings_AsPharmacist_IsRefused()
    {
        // Pharmacist lacks ManageSettings (plan.md §4).
        _sut.SetRole(UserRole.Pharmacist);
        await _sut.NavigateToAsync(NavigationModule.Landing);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Settings);

        Assert.False(navigated);
        Assert.IsType<LandingViewModel>(_sut.CurrentViewModel);
    }

    [Fact]
    public async Task NavigateToPurchases_WithDoPurchases_SetsPurchaseViewModel()
    {
        // Owner + Pharmacist have DoPurchases (plan.md §4).
        _sut.SetRole(UserRole.Owner);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Purchases);

        Assert.True(navigated);
        Assert.IsType<PurchaseViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Purchases, _sut.CurrentModule);
    }

    [Fact]
    public async Task NavigateToPurchases_AsCashier_IsRefused()
    {
        // Cashier lacks DoPurchases (plan.md §4); the current view stays put.
        _sut.SetRole(UserRole.Cashier);
        await _sut.NavigateToAsync(NavigationModule.Landing);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Purchases);

        Assert.False(navigated);
        Assert.IsType<LandingViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Landing, _sut.CurrentModule);
    }

    [Fact]
    public async Task NavigateToBilling_WithDoBilling_SetsBillingViewModel()
    {
        // All three roles have DoBilling (plan.md §4) — even a Cashier operates the POS.
        _sut.SetRole(UserRole.Cashier);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Billing);

        Assert.True(navigated);
        Assert.IsType<ApexPharma.Desktop.ViewModels.Billing.BillingViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Billing, _sut.CurrentModule);
    }

    [Fact]
    public async Task NavigateToInventory_WithViewStock_SetsInventoryViewModel()
    {
        // All roles have ViewStock (plan.md §4) — even a Cashier can view stock.
        _sut.SetRole(UserRole.Cashier);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Inventory);

        Assert.True(navigated);
        Assert.IsType<InventoryViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Inventory, _sut.CurrentModule);
    }

    [Fact]
    public async Task NavigateToMasters_WithManageProducts_SetsMastersViewModel()
    {
        // Owner has ManageProducts (plan.md §4).
        _sut.SetRole(UserRole.Owner);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Masters);

        Assert.True(navigated);
        Assert.IsType<MastersViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Masters, _sut.CurrentModule);
    }

    [Fact]
    public async Task NavigateToMasters_WithoutManageProducts_IsRefused()
    {
        // Cashier lacks ManageProducts (plan.md §4). Land somewhere first so we can prove
        // the refused navigation leaves the current view untouched.
        _sut.SetRole(UserRole.Cashier);
        await _sut.NavigateToAsync(NavigationModule.Landing);

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Masters);

        Assert.False(navigated);
        Assert.IsType<LandingViewModel>(_sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Landing, _sut.CurrentModule);
    }

    [Fact]
    public void CanNavigateTo_Masters_MatchesManageProductsPermission()
    {
        _sut.SetRole(UserRole.Owner);
        Assert.True(_sut.CanNavigateTo(NavigationModule.Masters));

        _sut.SetRole(UserRole.Pharmacist);
        Assert.True(_sut.CanNavigateTo(NavigationModule.Masters)); // Pharmacist has ManageProducts

        _sut.SetRole(UserRole.Cashier);
        Assert.False(_sut.CanNavigateTo(NavigationModule.Masters));
    }

    [Fact]
    public async Task Navigating_DisposesPreviousScope()
    {
        _sut.SetRole(UserRole.Owner);

        // First navigation: one scope created, none disposed yet (it is the active scope).
        await _sut.NavigateToAsync(NavigationModule.Landing);
        Assert.Equal(1, _scopeFactory.CreatedCount);
        Assert.Equal(0, _scopeFactory.DisposedCount);

        // Second navigation: a new scope is created and the PREVIOUS one disposed.
        await _sut.NavigateToAsync(NavigationModule.Billing);
        Assert.Equal(2, _scopeFactory.CreatedCount);
        Assert.Equal(1, _scopeFactory.DisposedCount);

        // Third navigation: same again — exactly one scope alive at a time.
        await _sut.NavigateToAsync(NavigationModule.Reports);
        Assert.Equal(3, _scopeFactory.CreatedCount);
        Assert.Equal(2, _scopeFactory.DisposedCount);
    }

    [Fact]
    public async Task RefusedNavigation_DoesNotCreateOrLeakScope()
    {
        _sut.SetRole(UserRole.Cashier);
        await _sut.NavigateToAsync(NavigationModule.Landing);
        int createdAfterLanding = _scopeFactory.CreatedCount;

        bool navigated = await _sut.NavigateToAsync(NavigationModule.Masters);

        Assert.False(navigated);
        // Refused before any scope was created — no new scope, nothing to dispose.
        Assert.Equal(createdAfterLanding, _scopeFactory.CreatedCount);
        Assert.Equal(0, _scopeFactory.DisposedCount);
    }

    [Fact]
    public async Task Dispose_DisposesActiveScope()
    {
        _sut.SetRole(UserRole.Owner);
        await _sut.NavigateToAsync(NavigationModule.Landing);
        Assert.Equal(0, _scopeFactory.DisposedCount);

        _sut.Dispose();

        // The active (last) scope is released on shutdown.
        Assert.Equal(1, _scopeFactory.DisposedCount);
    }

    [Fact]
    public async Task Navigating_WhileEarlierNavigationInFlight_LastNavigationWins_AndAllScopesDisposedOnce()
    {
        // Two controllable modules: the first navigation (Masters) awaits a SLOW activation;
        // the second (Billing) activates immediately. We start the slow one, begin the fast
        // one before it finishes, then release the slow one — the last click must win.
        var slow = new ControllableActivatableViewModel(completeImmediately: false);
        var fast = new ControllableActivatableViewModel(completeImmediately: true);

        var sut = _host.CreateNavigationService(_scopeFactory, (_, module) => module switch
        {
            NavigationModule.Masters => slow,
            _ => fast
        });
        sut.SetRole(UserRole.Owner);

        // Begin the slow navigation; it enters ActivateAsync and blocks on its gate.
        Task<bool> slowNav = sut.NavigateToAsync(NavigationModule.Masters);
        Assert.True(slow.ActivationStarted);
        Assert.False(slowNav.IsCompleted);

        // Second navigation arrives before the first completes and finishes right away.
        bool fastNavigated = await sut.NavigateToAsync(NavigationModule.Billing);
        Assert.True(fastNavigated);
        Assert.Same(fast, sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Billing, sut.CurrentModule);

        // Now let the earlier (superseded) navigation resume. It must stand down: report
        // failure and NOT swap its stale view in.
        slow.Release();
        bool slowNavigated = await slowNav;

        Assert.False(slowNavigated);
        Assert.Same(fast, sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Billing, sut.CurrentModule);

        // Two scopes were created (one per navigation). The superseded navigation disposed
        // its own scope on standing down; the winner's scope is still active.
        Assert.Equal(2, _scopeFactory.CreatedCount);
        Assert.Equal(1, _scopeFactory.DisposedCount);

        // Dispose releases the one remaining (winner's) scope. Every created scope is now
        // disposed exactly once — no leak, no double-dispose.
        sut.Dispose();
        Assert.Equal(2, _scopeFactory.DisposedCount);
    }

    [Fact]
    public async Task Navigation_WhenActivationThrows_ReportsFailure_KeepsPreviousView_AndDisposesFailedScope()
    {
        // Land on a good view first so we can prove a failing navigation leaves it intact.
        var landing = new ControllableActivatableViewModel(completeImmediately: true);
        var faulting = new ControllableActivatableViewModel(completeImmediately: true, fault: true);

        var sut = _host.CreateNavigationService(_scopeFactory, (_, module) => module switch
        {
            NavigationModule.Masters => faulting,
            _ => landing
        });
        sut.SetRole(UserRole.Owner);

        bool landed = await sut.NavigateToAsync(NavigationModule.Landing);
        Assert.True(landed);
        Assert.Same(landing, sut.CurrentViewModel);
        int disposedAfterLanding = _scopeFactory.DisposedCount;

        // Navigating to the faulting module must NOT propagate the exception; it returns
        // false and leaves the current (landing) view untouched.
        bool navigated = await sut.NavigateToAsync(NavigationModule.Masters);

        Assert.False(navigated);
        Assert.Same(landing, sut.CurrentViewModel);
        Assert.Equal(NavigationModule.Landing, sut.CurrentModule);

        // The failed navigation's own scope was disposed (its half-built DbContext released);
        // the still-active landing scope was NOT touched.
        Assert.Equal(disposedAfterLanding + 1, _scopeFactory.DisposedCount);
    }
}
