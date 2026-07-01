using System;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Invoicing;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Reporting;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Data;
using ApexPharma.Data.Repositories;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.Services;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Desktop.ViewModels.Billing;
using ApexPharma.Desktop.ViewModels.Inventory;
using ApexPharma.Desktop.ViewModels.Masters;
using ApexPharma.Desktop.ViewModels.Purchases;
using ApexPharma.Desktop.ViewModels.Reports;
using ApexPharma.Desktop.ViewModels.Settings;
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
        services.AddScoped<IGstService, GstService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IManufacturerService, ManufacturerService>();
        services.AddScoped<ISupplierService, SupplierService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IPurchaseService, PurchaseService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddSingleton<IReportExporter, ReportExporter>();

        // Receipt printer + report file service: no-op stubs so a navigation test never
        // launches a PDF viewer or writes export files.
        services.AddSingleton<IReceiptPrinter, NoOpReceiptPrinter>();
        services.AddSingleton<IReportFileService, NoOpReportFileService>();

        // Session (singleton, like the app).
        services.AddSingleton<ISessionContext, SessionContext>();

        // Content-region view-models resolved per navigation.
        services.AddTransient<LandingViewModel>();
        services.AddTransient<PlaceholderViewModel>();
        services.AddTransient<MastersViewModel>();
        services.AddTransient<CategoryListViewModel>();
        services.AddTransient<ManufacturerListViewModel>();
        services.AddTransient<SupplierListViewModel>();
        services.AddTransient<ProductListViewModel>();
        services.AddTransient<PurchaseViewModel>();
        services.AddTransient<InventoryViewModel>();
        services.AddTransient<BillingViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ReportsViewModel>();

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

/// <summary>Test stub that swallows the receipt without launching a PDF viewer.</summary>
internal sealed class NoOpReceiptPrinter : IReceiptPrinter
{
    public System.Threading.Tasks.Task<string> PreviewAsync(byte[] pdfBytes, string billNo)
        => System.Threading.Tasks.Task.FromResult(string.Empty);
}

/// <summary>Test stub that swallows report exports without writing files or launching a viewer.</summary>
internal sealed class NoOpReportFileService : IReportFileService
{
    public System.Threading.Tasks.Task<string> SaveCsvAsync(string csv, string baseName)
        => System.Threading.Tasks.Task.FromResult(string.Empty);

    public System.Threading.Tasks.Task<string> SavePdfAsync(byte[] pdfBytes, string baseName)
        => System.Threading.Tasks.Task.FromResult(string.Empty);
}
