using System.Threading.Tasks;

namespace ApexPharma.Desktop.Services;

/// <summary>
/// Turns a generated receipt PDF into something the operator sees: opens it for preview/print in
/// the OS default PDF handler (from where the counter's thermal printer is one click away).
/// Isolated behind an interface so the billing view-model stays testable and the file/printer I/O
/// lives in one place (plan.md §8 layering, §13 printing). A silent send-to-default-printer path
/// can be added here later without touching the view-model.
/// </summary>
public interface IReceiptPrinter
{
    /// <summary>
    /// Writes <paramref name="pdfBytes"/> to a temporary file named after the bill and opens it in
    /// the default PDF viewer for preview/print. Returns the path written, or throws on I/O failure
    /// (the caller surfaces it as a status message).
    /// </summary>
    Task<string> PreviewAsync(byte[] pdfBytes, string billNo);
}
