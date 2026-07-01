using System.Globalization;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;

namespace ApexPharma.Application.Services.Invoicing;

/// <summary>
/// Concrete GST-invoice service (plan.md §11, §14). Assembles a layout-agnostic
/// <see cref="InvoiceModel"/> from a persisted <see cref="Sale"/> (with its lines, batches, and
/// products) and the pharmacy profile from <see cref="ISettingsService"/>, then renders the
/// thermal (80mm) receipt via <see cref="ThermalReceiptDocument"/> (QuestPDF). Keeping the model
/// build separate from rendering makes the compliant figures — GSTIN, DL, bill no, CGST/SGST
/// breakup, grand total — unit-testable without inspecting a PDF (plan.md §12). No money rule is
/// recomputed here: the invoice reflects the authoritative figures the billing engine stored.
/// </summary>
public sealed class InvoiceService : IInvoiceService
{
    private readonly ApexPharmaDbContext _db;
    private readonly ISettingsService _settings;

    public InvoiceService(ApexPharmaDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<MasterResult<InvoiceModel>> BuildInvoiceAsync(int saleId, CancellationToken cancellationToken = default)
    {
        Sale? sale = await _db.Sales
            .Include(s => s.Customer)
            .Include(s => s.CreatedByUser)
            .Include(s => s.Items).ThenInclude(i => i.Product)
            .Include(s => s.Items).ThenInclude(i => i.Batch)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SaleId == saleId, cancellationToken);

        if (sale is null)
        {
            return MasterResult<InvoiceModel>.Fail($"Sale {saleId} was not found.");
        }

        PharmacyProfile profile = await _settings.GetProfileAsync(cancellationToken);

        // Build the printed lines from the persisted SaleItems (the batch carries the printed
        // batch-no + expiry; the product carries name + HSN). Rate/discount/amount are taken as
        // stored so the receipt foots to the header exactly.
        var lines = sale.Items
            .Select(i => new InvoiceLine
            {
                ProductName = i.Product?.Name ?? "(unknown)",
                BatchNo = i.Batch?.BatchNo ?? string.Empty,
                Expiry = i.Batch?.ExpiryDate ?? default,
                HsnCode = i.Product?.HsnCode,
                Qty = i.Qty,
                Rate = i.Rate,
                Discount = i.Discount,
                GstRate = i.GstRate,
                Amount = i.LineTotal,
            })
            .ToList();

        // Tax summary grouped by GST rate (and HSN where present) — the CGST/SGST breakup a GST
        // invoice must show (plan.md §14). Taxable must be the POST-bill-discount net so the block
        // foots to the header: Σ(Taxable) == sale.Subtotal. Each SaleItem.Discount already folds in
        // BOTH the line discount AND the apportioned share of the whole-bill discount (BillingService
        // stores the combined discount per lot), so the item's net taxable is simply gross − discount.
        // We derive it from that stored net rather than from LineTotal − Cgst − Sgst, because the
        // whole-rupee round-off is folded into the last lot's LineTotal only — using LineTotal would
        // carry that round-off into the taxable and break the footing. CGST/SGST are summed from the
        // stored per-line figures, so each band still foots to the header CGST/SGST.
        var taxSummary = sale.Items
            .GroupBy(i => new { i.GstRate, Hsn = i.Product?.HsnCode })
            .Select(g => new InvoiceTaxSummaryRow
            {
                GstRate = g.Key.GstRate,
                HsnCode = g.Key.Hsn,
                Taxable = g.Sum(i => (i.Rate * i.Qty) - i.Discount),
                Cgst = g.Sum(i => i.Cgst),
                Sgst = g.Sum(i => i.Sgst),
            })
            .OrderBy(r => r.GstRate)
            .ToList();

        bool hasScheduled = sale.Items.Any(i =>
            i.Product?.Schedule is DrugSchedule.H or DrugSchedule.H1 or DrugSchedule.X);

        var model = new InvoiceModel
        {
            PharmacyName = profile.PharmacyName,
            AddressLine = profile.AddressLine,
            CityState = JoinNonEmpty(", ", profile.City, profile.State),
            Gstin = profile.Gstin,
            DlNumber = profile.DlNumber,
            Phone = profile.Phone,

            BillNo = sale.BillNo,
            BillDate = sale.BillDate,
            CashierName = sale.CreatedByUser?.FullName is { Length: > 0 } fn ? fn : (sale.CreatedByUser?.Username ?? string.Empty),
            CustomerName = sale.Customer?.Name,
            CustomerPhone = sale.Customer?.Phone,
            PaymentMode = sale.PaymentMode,
            DoctorName = sale.DoctorName,
            PrescriptionRef = sale.PrescriptionRef,
            HasScheduledDrug = hasScheduled,

            Lines = lines,
            TaxSummary = taxSummary,

            Subtotal = sale.Subtotal,
            Discount = sale.Discount,
            Cgst = sale.Cgst,
            Sgst = sale.Sgst,
            RoundOff = sale.RoundOff,
            Total = sale.Total,
            TotalQty = sale.Items.Sum(i => i.Qty),

            Footer = profile.InvoiceFooter,
        };

        return MasterResult<InvoiceModel>.Ok(model);
    }

    /// <inheritdoc />
    public async Task<MasterResult<byte[]>> GenerateReceiptPdf(int saleId, CancellationToken cancellationToken = default)
    {
        MasterResult<InvoiceModel> built = await BuildInvoiceAsync(saleId, cancellationToken);
        if (!built.Succeeded)
        {
            return MasterResult<byte[]>.Fail(built.Error!);
        }

        // Render the 80mm thermal receipt. QuestPDF requires its license be set once before any
        // document is generated (done at app startup / in the test fixture) or this throws.
        var document = new ThermalReceiptDocument(built.Value!);
        byte[] pdf = document.GeneratePdf();
        return MasterResult<byte[]>.Ok(pdf);
    }

    private static string JoinNonEmpty(string separator, params string[] parts)
        => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>Formats money with the invariant culture so amounts print consistently (e.g. 1234.50).</summary>
    internal static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
