namespace ApexPharma.Application.Services.Ledger;

/// <summary>
/// A single ledger transaction gathered from a party's history, before window-splitting and
/// running-balance accumulation. <see cref="Debit"/> increases the outstanding balance,
/// <see cref="Credit"/> reduces it. <see cref="SortId"/> + <see cref="TypeRank"/> give a stable
/// tiebreak when two rows share the same <see cref="Date"/> so the statement order is deterministic.
/// </summary>
/// <param name="Date">Transaction date (UTC).</param>
/// <param name="DocType">Human-readable row type shown in the statement.</param>
/// <param name="RefNo">Reference shown in the statement (bill/invoice/receipt number).</param>
/// <param name="Debit">Amount added to the outstanding balance (0 for a credit row).</param>
/// <param name="Credit">Amount subtracted from the outstanding balance (0 for a debit row).</param>
/// <param name="SortId">The source row's primary key — stable per-type tiebreak.</param>
/// <param name="TypeRank">A small per-type rank so same-date rows order sales → returns → receipts/payments deterministically.</param>
internal readonly record struct LedgerTxn(
    DateTime Date,
    string DocType,
    string RefNo,
    decimal Debit,
    decimal Credit,
    int SortId,
    int TypeRank);

/// <summary>
/// Shared, provider-agnostic statement builder for both party ledgers (plan.md §3, §11). Kept
/// separate so the customer and supplier services derive their running balances identically:
/// materialise the party's transactions, fold everything strictly before the window into the
/// opening balance (carry-forward), emit a synthetic opening row, then accumulate the in-window
/// rows in date order with a stable tiebreak. All arithmetic is in memory (decimal), avoiding the
/// SQLite EF provider's brittle grouped-decimal SUM.
/// </summary>
internal static class LedgerMath
{
    /// <summary>
    /// Builds a <see cref="PartyStatement"/> filtered over the half-open UTC instant window
    /// [<paramref name="fromUtc"/>, <paramref name="toUtcExclusive"/>) on the transactions'
    /// UTC-stamped dates, while displaying the operator-facing LOCAL
    /// [<paramref name="displayFrom"/>, <paramref name="displayTo"/>] range. Splitting the filter
    /// bounds (UTC) from the display dates (local) lets a transaction stamped near local midnight
    /// bucket into the day the operator expects without the header showing a shifted date (plan.md §11).
    /// <para>
    /// Opening balance = <paramref name="openingConstant"/> (e.g. a supplier's stored opening
    /// balance; 0 for a customer) + the net (Σdebit − Σcredit) of every transaction dated strictly
    /// before <paramref name="fromUtc"/>. The first emitted row is a synthetic "Opening balance" line
    /// (debit = credit = 0), dated the local <paramref name="displayFrom"/>. Then each in-window
    /// transaction, ordered by date → type-rank → id, advances the running balance (debit adds,
    /// credit subtracts). The closing balance is the last running balance.
    /// </para>
    /// </summary>
    public static PartyStatement BuildStatement(
        string partyName,
        decimal openingConstant,
        IReadOnlyList<LedgerTxn> transactions,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        DateTime displayFrom,
        DateTime displayTo)
    {
        // Carry-forward: everything strictly before the window folds into the opening balance.
        decimal opening = openingConstant;
        foreach (LedgerTxn t in transactions)
        {
            if (t.Date < fromUtc)
            {
                opening += t.Debit - t.Credit;
            }
        }

        var rows = new List<PartyStatementRow>
        {
            // Synthetic first row so the statement reads with its carried-forward starting point.
            new(displayFrom, "Opening balance", string.Empty, 0m, 0m, opening),
        };

        // In-window transactions, ordered deterministically (date, then per-type rank, then id).
        IEnumerable<LedgerTxn> inWindow = transactions
            .Where(t => t.Date >= fromUtc && t.Date < toUtcExclusive)
            .OrderBy(t => t.Date)
            .ThenBy(t => t.TypeRank)
            .ThenBy(t => t.SortId);

        decimal running = opening;
        foreach (LedgerTxn t in inWindow)
        {
            running += t.Debit - t.Credit;
            rows.Add(new PartyStatementRow(t.Date, t.DocType, t.RefNo, t.Debit, t.Credit, running));
        }

        return new PartyStatement(partyName, opening, rows, running, displayFrom, displayTo);
    }
}
