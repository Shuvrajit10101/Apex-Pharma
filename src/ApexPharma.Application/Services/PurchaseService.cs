using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services;

/// <summary>
/// Concrete purchase / GRN service (plan.md §6.1, §9, §12). Recording a purchase is the
/// stock-in path: in ONE ACID transaction it inserts the <see cref="Purchase"/> header and
/// <see cref="PurchaseItem"/> lines, then upserts a <see cref="Batch"/> per line (adding
/// quantity to an existing (product, batch-no) lot or creating a new one). Header money
/// totals are rolled up from the per-line GST computed by <see cref="IGstService"/>.
/// <para>
/// Every mutation re-checks <see cref="Permission.DoPurchases"/> (plan.md §4) and returns a
/// <see cref="MasterResult"/> for expected validation failures instead of throwing
/// (plan.md §6.2). Any invalid line fails the whole purchase — nothing is persisted.
/// Purchase returns decrement the exact batch and never drive stock negative (plan.md §12).
/// </para>
/// </summary>
public class PurchaseService : IPurchaseService
{
    /// <summary>The legal Indian GST slabs a purchase line's rate may take (plan.md §6.1).</summary>
    private static readonly decimal[] ValidGstRates = { 0m, 5m, 12m, 18m, 28m };

    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;
    private readonly IGstService _gst;

    public PurchaseService(ApexPharmaDbContext db, IAuthService auth, IGstService gst)
    {
        _db = db;
        _auth = auth;
        _gst = gst;
    }

    /// <inheritdoc />
    public async Task<MasterResult<Purchase>> RecordPurchaseAsync(
        PurchaseInput input, int userId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.DoPurchases))
        {
            return MasterResult<Purchase>.Fail("You do not have permission to record purchases.");
        }

        if (input is null)
        {
            return MasterResult<Purchase>.Fail("Purchase details are required.");
        }

        // Header-level validation.
        Supplier? supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.SupplierId == input.SupplierId, cancellationToken);
        if (supplier is null)
        {
            return MasterResult<Purchase>.Fail("A valid supplier is required.");
        }

        if (input.Lines is null || input.Lines.Count == 0)
        {
            return MasterResult<Purchase>.Fail("A purchase must have at least one line item.");
        }

        // Validate every line BEFORE we touch the database, so a bad line means nothing is
        // persisted (the transaction below is the second guarantee, not the only one).
        DateTime today = DateTime.UtcNow.Date;
        foreach (PurchaseLineInput line in input.Lines)
        {
            string? error = await ValidateLineAsync(line, today, cancellationToken);
            if (error is not null)
            {
                return MasterResult<Purchase>.Fail(error);
            }
        }

        // One ACID transaction: header + lines + batch stock-in all commit together or not
        // at all (plan.md §12 money/stock is transactional).
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var purchase = new Purchase
            {
                SupplierId = input.SupplierId,
                SupplierInvoiceNo = string.IsNullOrWhiteSpace(input.SupplierInvoiceNo)
                    ? null
                    : input.SupplierInvoiceNo.Trim(),
                InvoiceDate = input.InvoiceDate,
                CreatedBy = userId,
                CreatedAt = DateTime.UtcNow,
            };

            decimal subtotal = 0m;
            decimal gstAmount = 0m;

            foreach (PurchaseLineInput line in input.Lines)
            {
                string batchNo = line.BatchNo.Trim();

                decimal lineBase = line.PurchasePrice * line.Qty;
                GstResult gst = _gst.CalculateLineGst(lineBase, line.GstRate);
                subtotal += lineBase;
                gstAmount += gst.TotalGst;

                purchase.Items.Add(new PurchaseItem
                {
                    ProductId = line.ProductId,
                    BatchNo = batchNo,
                    ExpiryDate = line.ExpiryDate,
                    Qty = line.Qty,
                    PurchasePrice = line.PurchasePrice,
                    Mrp = line.Mrp,
                    GstRate = line.GstRate,
                });

                await UpsertBatchAsync(line, batchNo, input.SupplierId, cancellationToken);
            }

            purchase.Subtotal = subtotal;
            purchase.GstAmount = gstAmount;
            purchase.Total = subtotal + gstAmount;

            await _db.Purchases.AddAsync(purchase, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return MasterResult<Purchase>.Ok(purchase);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MasterResult<PurchaseReturn>> ProcessPurchaseReturnAsync(
        int purchaseId, int batchId, decimal qty, string? reason, int userId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.DoPurchases))
        {
            return MasterResult<PurchaseReturn>.Fail("You do not have permission to process purchase returns.");
        }

        if (qty <= 0)
        {
            return MasterResult<PurchaseReturn>.Fail("Return quantity must be greater than zero.");
        }

        bool purchaseExists = await _db.Purchases.AnyAsync(p => p.PurchaseId == purchaseId, cancellationToken);
        if (!purchaseExists)
        {
            return MasterResult<PurchaseReturn>.Fail("The purchase to return against does not exist.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Load the batch inside the transaction and decrement transactionally so two
            // concurrent returns can't both pass the non-negative check (plan.md §12).
            Batch? batch = await _db.Batches.FirstOrDefaultAsync(b => b.BatchId == batchId, cancellationToken);
            if (batch is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<PurchaseReturn>.Fail("The batch to return does not exist.");
            }

            // Never let stock go negative — reject an over-return (plan.md §6.2, §12).
            if (qty > batch.QtyOnHand)
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<PurchaseReturn>.Fail(
                    $"Cannot return {qty:0.##} units — only {batch.QtyOnHand:0.##} are on hand for this batch.");
            }

            batch.QtyOnHand -= qty;

            var purchaseReturn = new PurchaseReturn
            {
                PurchaseId = purchaseId,
                BatchId = batchId,
                Qty = qty,
                Amount = batch.PurchasePrice * qty,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                Date = DateTime.UtcNow,
                CreatedBy = userId,
            };

            await _db.PurchaseReturns.AddAsync(purchaseReturn, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return MasterResult<PurchaseReturn>.Ok(purchaseReturn);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Purchase>> GetRecentPurchasesAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            take = 50;
        }

        return await _db.Purchases.AsNoTracking()
            .Include(p => p.Supplier)
            .Include(p => p.Items)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.PurchaseId)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Batch?> FindBatchAsync(int productId, string batchNo, CancellationToken cancellationToken = default)
    {
        batchNo = batchNo?.Trim() ?? string.Empty;
        return await _db.Batches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.ProductId == productId && b.BatchNo == batchNo, cancellationToken);
    }

    /// <summary>
    /// Batch upsert (the stock-in): add the received quantity to an existing lot with the
    /// same (product, batch-no), or create a new <see cref="Batch"/> when none exists —
    /// SalePrice defaults to the line's MRP (plan.md §6.1). Called inside the purchase
    /// transaction so the new/updated batch commits with the header + lines.
    /// </summary>
    private async Task UpsertBatchAsync(PurchaseLineInput line, string batchNo, int supplierId, CancellationToken cancellationToken)
    {
        Batch? existing = await _db.Batches
            .FirstOrDefaultAsync(b => b.ProductId == line.ProductId && b.BatchNo == batchNo, cancellationToken);

        if (existing is not null)
        {
            existing.QtyOnHand += line.Qty;
            return;
        }

        var batch = new Batch
        {
            ProductId = line.ProductId,
            BatchNo = batchNo,
            ExpiryDate = line.ExpiryDate,
            Mrp = line.Mrp,
            PurchasePrice = line.PurchasePrice,
            SalePrice = line.Mrp, // default sale price to MRP; can be tuned later (plan.md §6.1)
            QtyOnHand = line.Qty,
            SupplierId = supplierId,
            ReceivedDate = DateTime.UtcNow,
        };

        await _db.Batches.AddAsync(batch, cancellationToken);
    }

    /// <summary>
    /// Per-line validation (plan.md §6.1, §14). Returns null when valid, otherwise a clear
    /// message. Rejects already-expired goods (expiry on/before today) so expired stock is
    /// never received. The product must exist and be active.
    /// </summary>
    private async Task<string?> ValidateLineAsync(PurchaseLineInput line, DateTime today, CancellationToken cancellationToken)
    {
        if (line is null)
        {
            return "A purchase line is missing.";
        }

        if (line.Qty <= 0)
        {
            return "Each line quantity must be greater than zero.";
        }

        if (line.PurchasePrice < 0)
        {
            return "Purchase price cannot be negative.";
        }

        if (line.Mrp < 0)
        {
            return "MRP cannot be negative.";
        }

        if (!ValidGstRates.Contains(line.GstRate))
        {
            return "GST rate must be one of the Indian slabs: 0, 5, 12, 18, or 28%.";
        }

        if (string.IsNullOrWhiteSpace(line.BatchNo))
        {
            return "Batch number is required on every line.";
        }

        // Never stock already-expired goods: expiry must be strictly after today (plan.md §14).
        if (line.ExpiryDate.Date <= today)
        {
            return "Expiry date must be in the future — expired stock cannot be received.";
        }

        Product? product = await _db.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProductId == line.ProductId, cancellationToken);
        if (product is null)
        {
            return "A line references a product that does not exist.";
        }

        if (!product.IsActive)
        {
            return $"Product '{product.Name}' is inactive and cannot be stocked in.";
        }

        return null;
    }
}
