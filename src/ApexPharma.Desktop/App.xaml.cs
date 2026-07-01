using System;
using System.IO;
using System.Windows;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Desktop.ViewModels.Masters;
using ApexPharma.Desktop.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPharma.Desktop;

/// <summary>
/// WPF application entry point. Builds the dependency-injection container (plan.md §8
/// layering: Presentation → Services → Data), migrates and seeds the local SQLite
/// database, then shows the login-gated shell.
/// </summary>
/// <remarks>
/// The base type is fully qualified because the project reference
/// <c>ApexPharma.Application</c> makes the bare name <c>Application</c> resolve to that
/// namespace instead of <see cref="System.Windows.Application"/> (CS0118).
/// </remarks>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    /// <summary>
    /// Absolute path to the runtime database under
    /// <c>%LocalAppData%\ApexPharma\apexpharma.db</c> — deliberately NOT the repo copy,
    /// which is only used by the EF design-time tools. Patient/business data lives in
    /// the user's local profile, never in source control (plan.md §14).
    /// </summary>
    private static string RuntimeDbPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ApexPharma");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "apexpharma.db");
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var provider = ConfigureServices();
        _services = provider;

        // Create/upgrade the schema, then seed roles + the bootstrap Owner. Both are
        // idempotent and safe to run on every launch (plan.md §13 auto-migration).
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApexPharmaDbContext>();
            db.Database.Migrate();

            var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
            DbInitializer.SeedAsync(db, auth.HashPassword).GetAwaiter().GetResult();
        }

        var login = provider.GetRequiredService<LoginWindow>();
        login.Show();
    }

    /// <summary>Registers the DbContext, repositories/UnitOfWork, services, and windows.</summary>
    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Data layer — DbContext + repositories/UnitOfWork. The SQLite wiring detail lives
        // in the Data layer (plan.md §8); we just point it at the runtime DB in
        // %LocalAppData% (never the repo copy).
        services.AddApexPharmaData(RuntimeDbPath);

        // Business services. AuthService is the concrete Phase-1 implementation; the
        // remaining services stay on their stubs until their phase (plan.md §15).
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IGstService, GstService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IPurchaseService, PurchaseService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IBackupService, BackupService>();

        // Master-data services (Phase 1b — plan.md §6.1). Concrete implementations over
        // the shared DbContext; RBAC-gated on the acting user's role.
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IManufacturerService, ManufacturerService>();
        services.AddScoped<ISupplierService, SupplierService>();
        services.AddScoped<IProductService, ProductService>();

        // Presentation — view-models and windows.
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginWindow>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();

        // Masters area — one hosting window + its view-models (plan.md §10). Transient so
        // each open gets a fresh scoped DbContext via the VMs' service dependencies.
        services.AddTransient<MastersWindow>();
        services.AddTransient<MastersViewModel>();
        services.AddTransient<CategoryListViewModel>();
        services.AddTransient<ManufacturerListViewModel>();
        services.AddTransient<SupplierListViewModel>();
        services.AddTransient<ProductListViewModel>();

        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
