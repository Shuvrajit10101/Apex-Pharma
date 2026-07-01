using System;
using System.Threading.Tasks;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Desktop.ViewModels.Masters;
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

    [Theory]
    [InlineData(NavigationModule.Billing, "Billing")]
    [InlineData(NavigationModule.Inventory, "Inventory")]
    [InlineData(NavigationModule.Purchases, "Purchases")]
    [InlineData(NavigationModule.Reports, "Reports")]
    [InlineData(NavigationModule.Settings, "Settings")]
    public async Task NavigateToStubModule_SetsPlaceholder_WithModuleLabel(NavigationModule module, string label)
    {
        _sut.SetRole(UserRole.Owner);

        bool navigated = await _sut.NavigateToAsync(module);

        Assert.True(navigated);
        var placeholder = Assert.IsType<PlaceholderViewModel>(_sut.CurrentViewModel);
        Assert.Equal(label, placeholder.ModuleName);
        Assert.Contains("coming in a later phase", placeholder.Headline);
        Assert.Equal(module, _sut.CurrentModule);
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
}
