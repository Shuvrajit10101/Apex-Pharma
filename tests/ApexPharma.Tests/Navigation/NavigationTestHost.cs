using System;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Data.Repositories;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Desktop.ViewModels.Masters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPharma.Tests.Navigation;

/// <summary>
/// Builds a real DI container for navigation tests, mirroring the app's registrations
/// (plan.md §10) but over a shared <b>in-memory SQLite</b> connection so scoped
/// <see cref="ApexPharmaDbContext"/> resolution and Masters activation work end-to-end.
/// The connection is kept open for the host's lifetime; disposing the host drops the DB.
/// </summary>
public sealed class NavigationTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public NavigationTestHost()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        // Data layer over the shared in-memory connection (scoped DbContext, like the app).
        services.AddDbContext<ApexPharmaDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Services.
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IManufacturerService, ManufacturerService>();
        services.AddScoped<ISupplierService, SupplierService>();
        services.AddScoped<IProductService, ProductService>();

        // Content-region view-models resolved per navigation.
        services.AddTransient<LandingViewModel>();
        services.AddTransient<PlaceholderViewModel>();
        services.AddTransient<MastersViewModel>();
        services.AddTransient<CategoryListViewModel>();
        services.AddTransient<ManufacturerListViewModel>();
        services.AddTransient<SupplierListViewModel>();
        services.AddTransient<ProductListViewModel>();

        _provider = services.BuildServiceProvider();

        // Create the schema once so Masters activation (which reads the catalog) succeeds.
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApexPharmaDbContext>().Database.EnsureCreated();
    }

    public IServiceProvider Provider => _provider;

    /// <summary>An <see cref="IAuthService"/> (RBAC is pure logic) for direct assertions.</summary>
    public IAuthService Auth => _provider.GetRequiredService<IAuthService>();

    /// <summary>A scope factory that records scope create/dispose counts for probing.</summary>
    public ProbingScopeFactory CreateProbingScopeFactory() => new(_provider);

    /// <summary>A navigation service wired to the probing factory and the real auth service.</summary>
    public NavigationService CreateNavigationService(ProbingScopeFactory scopeFactory)
        => new(scopeFactory, Auth);

    /// <summary>
    /// A navigation service with an injectable module→view-model resolver (internal test
    /// seam), so tests can supply controllable — slow or throwing — activations to exercise
    /// the re-entrancy guard and the non-fatal activation-failure handling.
    /// </summary>
    public NavigationService CreateNavigationService(
        ProbingScopeFactory scopeFactory,
        Func<IServiceProvider, NavigationModule, object> resolve)
        => new(scopeFactory, Auth, resolve);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
