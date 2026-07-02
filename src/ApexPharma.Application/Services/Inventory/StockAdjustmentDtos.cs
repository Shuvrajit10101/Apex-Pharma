using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Inventory;

/// <summary>
/// A batch offered for adjustment / write-off in the Stock Adjustment UI (plan.md §6.1, §10):
/// which lot it is, its product, on-hand quantity, expiry, and per-unit costs so the operator
/// can see value at risk. A read-model — no mutation happens here (plan.md §8).
/// </summary>
public sealed record AdjustableBatch(
    int BatchId,
    int ProductId,
    string ProductName,
    string BatchNo,
    DateTime ExpiryDate,
    decimal QtyOnHand,
    decimal PurchasePrice,
    decimal Mrp,
    bool IsExpired);

/// <summary>
/// A single expired-batch write-off outcome inside a bulk run (plan.md §6.1). Carries the value
/// lost at both purchase cost and MRP so the owner sees the financial impact of the write-off.
/// </summary>
public sealed record ExpiryWriteOffLine(
    int BatchId,
    int ProductId,
    string ProductName,
    string BatchNo,
    DateTime ExpiryDate,
    decimal QtyWrittenOff,
    decimal ValueAtCost,
    decimal ValueAtMrp);

/// <summary>
/// Totals for a bulk expiry write-off (plan.md §6.1): how many batches/units were written off and
/// the total value lost at purchase cost and at MRP, plus the per-batch breakdown.
/// </summary>
public sealed record ExpiryWriteOffSummary(
    int BatchCount,
    decimal TotalQty,
    decimal TotalValueAtCost,
    decimal TotalValueAtMrp,
    IReadOnlyList<ExpiryWriteOffLine> Lines);

/// <summary>
/// One recent stock-adjustment row for the audit/history grid (plan.md §6.1): when, which product
/// and batch, the reason type, the signed quantity delta, the free-text reason, and who did it.
/// </summary>
public sealed record AdjustmentHistoryRow(
    int AdjustmentId,
    DateTime Date,
    int ProductId,
    string ProductName,
    int BatchId,
    string BatchNo,
    AdjustmentType Type,
    decimal QtyDelta,
    string? Reason,
    int CreatedBy,
    string CreatedByName);
