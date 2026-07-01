using System.Runtime.CompilerServices;
using QuestPDF.Infrastructure;

namespace ApexPharma.Tests;

/// <summary>
/// Sets the QuestPDF Community license once for the whole test assembly. QuestPDF throws at
/// render time unless a license is configured; production does the same at app startup
/// (App.OnStartup). A <see cref="ModuleInitializerAttribute"/> guarantees this runs before any
/// InvoiceService render test executes, regardless of test order.
/// </summary>
internal static class QuestPdfLicense
{
    [ModuleInitializer]
    internal static void Init()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }
}
