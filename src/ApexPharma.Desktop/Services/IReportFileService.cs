using System.Threading.Tasks;

namespace ApexPharma.Desktop.Services;

/// <summary>
/// Writes report exports to disk and opens them (plan.md §11). Keeps file I/O out of the
/// Reports view-model (plan.md §8): the view-model produces CSV text / PDF bytes via the
/// exporter and hands them here to persist under the user's Reports folder and open in the
/// default handler, leaving a kept copy the owner/accountant can re-open.
/// </summary>
public interface IReportFileService
{
    /// <summary>Saves CSV text as UTF-8 (with BOM, so Excel reads Unicode) and opens it. Returns the path.</summary>
    Task<string> SaveCsvAsync(string csv, string baseName);

    /// <summary>Saves PDF bytes and opens them in the default viewer. Returns the path.</summary>
    Task<string> SavePdfAsync(byte[] pdfBytes, string baseName);
}
