using System;
using System.Collections.Generic;
using ApexPharma.Application.Services.Ledger;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Unit tests for <see cref="LedgerMath.BuildStatement"/> (plan.md §3, §11) — the shared,
/// provider-agnostic statement builder. <see cref="LedgerTxn"/> and <see cref="LedgerMath"/> are
/// internal; the test project has <c>InternalsVisibleTo</c> access. Covers the deterministic
/// same-date tiebreak: rows sharing an identical <c>Date</c> must emit in exact
/// <c>Date → TypeRank → SortId</c> order with the running balance correct at each step.
/// </summary>
public class LedgerMathTests
{
    [Fact]
    public void BuildStatement_SameDate_OrdersByTypeRankThenSortId_WithRunningBalance()
    {
        // All five transactions share ONE identical date. Constructed OUT of the expected emit
        // order so a stable sort by (Date → TypeRank → SortId) is actually exercised.
        DateTime d = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        // Expected emit order after opening:
        //   TypeRank 0 (debits): SortId 5 (100), then SortId 9 (50)
        //   TypeRank 1 (credit): SortId 2 (30)
        //   TypeRank 2 (credits): SortId 1 (40), then SortId 7 (20)
        var txns = new List<LedgerTxn>
        {
            new(d, "Payment",       "P7",  Debit: 0m,   Credit: 20m, SortId: 7, TypeRank: 2),
            new(d, "Purchase",      "PU9", Debit: 50m,  Credit: 0m,  SortId: 9, TypeRank: 0),
            new(d, "Purchase ret",  "R2",  Debit: 0m,   Credit: 30m, SortId: 2, TypeRank: 1),
            new(d, "Purchase",      "PU5", Debit: 100m, Credit: 0m,  SortId: 5, TypeRank: 0),
            new(d, "Payment",       "P1",  Debit: 0m,   Credit: 40m, SortId: 1, TypeRank: 2),
        };

        PartyStatement s = LedgerMath.BuildStatement(
            "Acme", openingConstant: 0m, txns, fromUtc: d, toUtcExclusive: d.AddDays(1),
            displayFrom: d, displayTo: d);

        // Opening row + five in-window rows.
        Assert.Equal(6, s.Rows.Count);
        Assert.Equal("Opening balance", s.Rows[0].DocType);
        Assert.Equal(0m, s.Rows[0].RunningBalance);

        // Row 1: Purchase 100 (TypeRank 0, SortId 5) → 100.
        Assert.Equal("PU5", s.Rows[1].RefNo);
        Assert.Equal(100m, s.Rows[1].RunningBalance);

        // Row 2: Purchase 50 (TypeRank 0, SortId 9) → 150.
        Assert.Equal("PU9", s.Rows[2].RefNo);
        Assert.Equal(150m, s.Rows[2].RunningBalance);

        // Row 3: Purchase return 30 (TypeRank 1, SortId 2) → 120.
        Assert.Equal("R2", s.Rows[3].RefNo);
        Assert.Equal(120m, s.Rows[3].RunningBalance);

        // Row 4: Payment 40 (TypeRank 2, SortId 1) → 80.
        Assert.Equal("P1", s.Rows[4].RefNo);
        Assert.Equal(80m, s.Rows[4].RunningBalance);

        // Row 5: Payment 20 (TypeRank 2, SortId 7) → 60.
        Assert.Equal("P7", s.Rows[5].RefNo);
        Assert.Equal(60m, s.Rows[5].RunningBalance);

        Assert.Equal(60m, s.ClosingBalance);
    }
}
