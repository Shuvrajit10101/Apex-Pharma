using System;
using System.IO;
using System.Windows;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Invoicing;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Reporting;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Data;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.ViewModels;
using ApexPharma.Desktop.ViewModels.Billing;
using ApexPharma.Desktop.ViewModels.Inventory;
using ApexPharma.Desktop.ViewModels.Masters;
using ApexPharma.Desktop.ViewModels.Purchases;
using ApexPharma.Desktop.ViewModels.Reports;
using ApexPharma.Desktop.ViewModels.Settings;
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

        // QuestPDF requires a license be configured before any document is generated or it throws
        // at runtime. Community is free for this single-store client's scale (well under the
        // revenue threshold) and is the correct choice here (plan.md §8 invoices/QuestPDF).
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        // Global safety net: a stray UI exception (e.g. a background data load faulting on
        // the dispatcher) must not hard-crash the counter app. Log it and show a friendly
        // message, then mark it handled so the app keeps running (plan.md §10, §12).
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var provider = ConfigureServices();
        _services = provider;

        // Create/upgrade the schema, then seed roles + the bootstrap Owner and the default
        // pharmacy settings. All are idempotent and safe to run on every launch (plan.md §13).
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApexPharmaDbContext>();
            db.Database.Migrate();

            var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
            DbInitializer.SeedAsync(db, auth.HashPassword).GetAwaiter().GetResult();

            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            settings.SeedDefaultsAsync().GetAwaiter().GetResult();
        }

        var login = provider.GetRequiredService<LoginWindow>();
        login.Show();
    }

    /// <summary>
    /// Last-resort handler for exceptions that reach the WPF dispatcher unhandled. Logs the
    /// error to <c>%LocalAppData%\ApexPharma\error.log</c>, shows the user a friendly
    /// message, and marks the exception handled so a stray UI fault never hard-crashes the
    /// counter app (plan.md §10, §12). This is the safety net beneath the per-navigation
    /// handling in <see cref="ViewModels.MainViewModel"/> — normal navigation failures are
    /// caught there; only the unexpected reaches here.
    /// </summary>
    private void OnDispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);

        MessageBox.Show(
            "Something went wrong, but Apex-Pharma will keep running. If this keeps happening, " +
            "please note what you were doing and contact support.",
            "Apex-Pharma",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // Handled: keep the app alive rather than tearing down the whole shell over one
        // non-fatal UI exception.
        e.Handled = true;
    }

    /// <summary>Appends an exception to the local error log (best-effort; never throws).</summary>
    private static void LogException(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ApexPharma");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "error.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never itself crash the crash-handler. Fall back to Trace so the
            // detail is still visible under a debugger.
            System.Diagnostics.Trace.WriteLine(ex);
        }
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
        services.AddSingleton<IReportExporter, ReportExporter>();
        services.AddScoped<IBackupService, BackupService>();

        // Settings (pharmacy profile) + GST invoice generation (Phase 1e — plan.md §6.1, §11, §14).
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IInvoiceService, InvoiceService>();

        // Master-data services (Phase 1b — plan.md §6.1). Concrete implementations over
        // the shared DbContext; RBAC-gated on the acting user's role.
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IManufacturerService, ManufacturerService>();
        services.AddScoped<ISupplierService, SupplierService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<ICustomerService, CustomerService>();

        // Navigation shell (plan.md §10). Singleton so it owns module scopes for the app's
        // lifetime: each NavigateTo creates a fresh DI scope, resolves the target module's
        // view-model from it, and disposes the previous module's scope. Disposed on exit.
        services.AddSingleton<INavigationService, NavigationService>();

        // Signed-in session (plan.md §4). Singleton, set once at login, so per-visit module
        // view-models can attribute mutations (e.g. a Purchase's CreatedBy) to the acting user.
        services.AddSingleton<ISessionContext, SessionContext>();

        // Presentation — shell view-models and windows.
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginWindow>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();

        // Content-region view-models resolved per navigation from a fresh scope (plan.md §10).
        services.AddTransient<LandingViewModel>();
        services.AddTransient<PlaceholderViewModel>();

        // Masters area — its view-models (plan.md §10). Resolved inside the navigation
        // service's per-visit DI scope so the MastersViewModel, its four child list
        // view-models, and the four master services share ONE freshly-created scoped
        // ApexPharmaDbContext that is disposed when the user navigates away.
        services.AddTransient<MastersViewModel>();
        services.AddTransient<CategoryListViewModel>();
        services.AddTransient<ManufacturerListViewModel>();
        services.AddTransient<SupplierListViewModel>();
        services.AddTransient<ProductListViewModel>();

        // Purchase / GRN (Phase 1c — plan.md §6.1, §9) and read-only Inventory view. Resolved
        // per navigation from a fresh scope so their scoped DbContext is fresh each visit and
        // disposed on navigating away (same lifetime discipline as the Masters area).
        services.AddTransient<PurchaseViewModel>();
        services.AddTransient<InventoryViewModel>();

        // Billing / POS (Phase 1d — plan.md §6.1, §9). Resolved per navigation from a fresh
        // scope so its scoped DbContext, customer service, and billing service share ONE
        // context disposed on navigating away (same lifetime discipline as the other modules).
        services.AddTransient<BillingViewModel>();

        // Settings (Phase 1e — plan.md §6.1). Owner-only pharmacy-profile editor, resolved per
        // navigation from a fresh scope (same lifetime discipline as the other modules).
        services.AddTransient<SettingsViewModel>();

        // Reports (Phase 1f — plan.md §11, §14). Read-only report hub (sales/profit, low-stock,
        // expiry, Schedule-H register, GST/HSN summary), gated on ViewReports (Owner + Pharmacist).
        // Resolved per navigation from a fresh scope (same lifetime discipline as other modules).
        services.AddTransient<ReportsViewModel>();

        // Receipt printing (Phase 1e — plan.md §13). Writes the generated PDF and opens it in the
        // default viewer for print/reprint; singleton because it is stateless file/printer I/O.
        services.AddSingleton<Services.IReceiptPrinter, Services.ReceiptPrinter>();

        // Report export (Phase 1f — plan.md §11). Writes CSV/PDF exports under the user's Reports
        // folder and opens them; singleton because it is stateless file I/O.
        services.AddSingleton<Services.IReportFileService, Services.ReportFileService>();

        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
