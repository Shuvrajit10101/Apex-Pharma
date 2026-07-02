using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A finalized day-end cash reconciliation for one store business-day (plan.md §3, §11 — Phase 2e).
/// <para>
/// Closing a day snapshots the full cash breakdown — opening float, cash sales, cash receipts, cash
/// refunds, cash supplier payments, and the derived <see cref="ExpectedCash"/> — alongside the
/// counted cash and the resulting <see cref="Variance"/>. The breakdown is stored (not just the
/// totals) so a closed day is <b>immutable</b>: a later back-dated cash transaction changes today's
/// live figures but can never alter what was reconciled and signed off for a prior day. Exactly one
/// close may exist per business-day (enforced by a UNIQUE index on <see cref="BusinessDate"/>).
/// </para>
/// <para>
/// <see cref="OpeningFloat"/> carries forward from the previous close's
/// <see cref="ClosingCarryForward"/>; <see cref="ClosingCarryForward"/> is the till float handed to
/// the next business-day (defaults to <see cref="CountedCash"/> when not overridden). Money columns
/// are <c>decimal(18,2)</c>; timestamps are stored UTC.
/// </para>
/// </summary>
public class DayEndClose
{
    public int DayEndCloseId { get; set; }

    /// <summary>
    /// The store business-day this close covers, date-floored (time component 00:00). UNIQUE — one
    /// close per store-day. Not the UTC/local boundary question (that's Phase 2g); the window over
    /// this date is computed the same half-open way every report uses.
    /// </summary>
    public DateTime BusinessDate { get; set; }

    /// <summary>Till float at the start of the day = the prior close's <see cref="ClosingCarryForward"/> (0 if none).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal OpeningFloat { get; set; }

    /// <summary>Cash IN from cash-mode sales over the day (snapshot).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal CashSales { get; set; }

    /// <summary>Cash IN from cash-mode customer receipts over the day (snapshot).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal CashReceipts { get; set; }

    /// <summary>Cash OUT for refunds on cash-mode parent sales over the day (snapshot).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal CashRefunds { get; set; }

    /// <summary>Cash OUT for cash-mode supplier payments over the day (snapshot).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal CashSupplierPayments { get; set; }

    /// <summary>
    /// Server-derived expected cash in the till =
    /// <c>OpeningFloat + CashSales + CashReceipts − CashRefunds − CashSupplierPayments</c>.
    /// Recomputed and snapshotted at close time; never trusted from the UI.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal ExpectedCash { get; set; }

    /// <summary>The physically counted cash entered by the closer.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal CountedCash { get; set; }

    /// <summary>Counted − Expected. Positive = over, negative = short, zero = balanced.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Variance { get; set; }

    /// <summary>Float carried into the next business-day (defaults to <see cref="CountedCash"/>).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal ClosingCarryForward { get; set; }

    /// <summary>Optional note; REQUIRED by the service when <see cref="Variance"/> is non-zero.</summary>
    public string? Note { get; set; }

    /// <summary>When the day was closed (stored UTC; the UI converts to local for display).</summary>
    public DateTime ClosedAt { get; set; }

    /// <summary>FK to the <see cref="User"/> who closed the day (audit — plan.md §4).</summary>
    public int CreatedBy { get; set; }
    public User? CreatedByUser { get; set; }
}
