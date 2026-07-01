using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ApexPharma.Desktop.Services;

/// <summary>
/// Default <see cref="IReportFileService"/>: writes exports under
/// <c>%LocalAppData%\ApexPharma\Reports</c> and opens them with the OS default handler (from
/// which the owner prints or forwards to the accountant). CSV is written UTF-8 with a BOM so
/// Excel reads non-ASCII correctly. File I/O is deliberately kept out of the view-model
/// (plan.md §8, §13).
/// </summary>
public sealed class ReportFileService : IReportFileService
{
    /// <inheritdoc />
    public async Task<string> SaveCsvAsync(string csv, string baseName)
    {
        string path = BuildPath(baseName, "csv");
        // UTF-8 WITH BOM so Excel/LibreOffice detect Unicode (rupee sign, patient names).
        await File.WriteAllTextAsync(path, csv ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Open(path);
        return path;
    }

    /// <inheritdoc />
    public async Task<string> SavePdfAsync(byte[] pdfBytes, string baseName)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        string path = BuildPath(baseName, "pdf");
        await File.WriteAllBytesAsync(path, pdfBytes);
        Open(path);
        return path;
    }

    private static string BuildPath(string baseName, string extension)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ApexPharma",
            "Reports");
        Directory.CreateDirectory(dir);

        string safeBase = string.IsNullOrWhiteSpace(baseName)
            ? "report"
            : string.Join("_", baseName.Split(Path.GetInvalidFileNameChars()));
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(dir, $"{safeBase}-{stamp}.{extension}");
    }

    private static void Open(string path)
    {
        // Open in the default handler (UseShellExecute so the OS picks Excel/PDF viewer). Never
        // let a missing handler crash the app — the file is already saved.
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // The export succeeded even if nothing is registered to open it; swallow.
        }
    }
}
