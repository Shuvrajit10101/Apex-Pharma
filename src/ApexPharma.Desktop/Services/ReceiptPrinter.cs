using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ApexPharma.Desktop.Services;

/// <summary>
/// Default <see cref="IReceiptPrinter"/>: writes the receipt PDF under
/// <c>%LocalAppData%\ApexPharma\Receipts</c> and opens it with the OS default PDF handler, from
/// which the operator prints (Ctrl+P) to the counter's thermal printer. Keeping the bytes on disk
/// also leaves a reprintable copy. File/printer I/O is deliberately kept out of the view-model
/// (plan.md §8, §13).
/// </summary>
public sealed class ReceiptPrinter : IReceiptPrinter
{
    /// <inheritdoc />
    public async Task<string> PreviewAsync(byte[] pdfBytes, string billNo)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ApexPharma",
            "Receipts");
        Directory.CreateDirectory(dir);

        // Sanitise the bill number for a filename; fall back to a timestamp if empty.
        string safeBill = string.IsNullOrWhiteSpace(billNo)
            ? DateTime.Now.ToString("yyyyMMdd-HHmmss")
            : string.Join("_", billNo.Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(dir, $"{safeBill}.pdf");

        await File.WriteAllBytesAsync(path, pdfBytes);

        // Open in the default PDF viewer (UseShellExecute so the OS picks the handler). The
        // operator prints from there — no printer driver integration needed for v1.
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });

        return path;
    }
}
