using ApexPharma.Application.Services.MasterData;

namespace ApexPharma.Application.Services.Invoicing;

/// <summary>
/// Generates GST-compliant invoices for a completed sale (plan.md §11, §14). Builds a
/// layout-agnostic <see cref="InvoiceModel"/> from the persisted sale + the pharmacy profile
/// (name, address, GSTIN, DL number, phone, footer), and renders it to a thermal (80mm) receipt
/// PDF via QuestPDF. The assembled model is exposed separately so the numbers — GSTIN, DL, bill
/// no, line totals, CGST/SGST breakup, grand total — are unit-testable without rendering pixels
/// (plan.md §12). An A5/A4 layout is a later addition over the same model (client deferred A4).
/// </summary>
public interface IInvoiceService
{
    /// <summary>
    /// Assembles the invoice data model for a saved sale (bill header + lines + tax summary +
    /// pharmacy header). Returns a failed <see cref="MasterResult{T}"/> when the sale id is
    /// unknown — expected failures are return values, not exceptions (plan.md §6.2).
    /// </summary>
    Task<MasterResult<InvoiceModel>> BuildInvoiceAsync(int saleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders the thermal (80mm) GST receipt for a saved sale to a PDF byte array. Returns a
    /// failed <see cref="MasterResult{T}"/> for an unknown sale id.
    /// </summary>
    Task<MasterResult<byte[]>> GenerateReceiptPdf(int saleId, CancellationToken cancellationToken = default);
}
