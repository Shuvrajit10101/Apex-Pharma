namespace ApexPharma.Application.Time;

/// <summary>
/// Converts an operator-facing LOCAL calendar-day range (the dates a user picks in a date picker,
/// which they read in the pharmacy's own timezone) into a half-open <b>UTC</b> instant window
/// <c>[FromUtc, ToUtcExclusive)</c> suitable for filtering <c>UtcNow</c>-stamped rows
/// (<c>Sale.BillDate</c>, receipts, payments, returns …).
/// <para>
/// Transactions are stamped in UTC, but every report/ledger/day-end window is chosen as a LOCAL
/// calendar day. Flooring a local date and treating it as a UTC bound mis-buckets rows near local
/// midnight (for IST, UTC+5:30). This helper does the conversion correctly and in ONE place, so the
/// day boundary behaves identically across the sales report, Schedule register, HSN/GSTR-1 windows,
/// the customer/supplier statements, and the day-end cash windows (plan.md §11, §14).
/// </para>
/// <para>
/// Pure and deterministic: no I/O and no ambient time — the <see cref="TimeZoneInfo"/> is passed in
/// explicitly (supplied at runtime by <see cref="ITimeZoneProvider"/>), so it is trivially testable
/// against any timezone independent of the host machine's timezone. For "India Standard Time", the
/// local day D maps to <c>[D-1 18:30Z, D 18:30Z)</c>.
/// </para>
/// </summary>
public static class DayWindow
{
    /// <summary>
    /// Maps the inclusive local-date range [<paramref name="fromLocalDate"/>,
    /// <paramref name="toLocalDate"/>] to a half-open UTC window <c>[FromUtc, ToUtcExclusive)</c>.
    /// Both inputs are floored to their date (time-of-day and <see cref="DateTime.Kind"/> are
    /// ignored — these are calendar dates, not instants); a reversed range is swapped so a caller
    /// can never accidentally get an empty window. <paramref name="toLocalDate"/> is made exclusive
    /// by adding one local day, so a transaction stamped at any UTC instant that falls on the local
    /// <paramref name="toLocalDate"/> is included. Both returned values have
    /// <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) ToUtcWindow(
        DateTime fromLocalDate, DateTime toLocalDate, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(tz);

        DateTime from = fromLocalDate.Date;
        DateTime to = toLocalDate.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        // Interpret each floored local date as a wall-clock instant in the pharmacy timezone
        // (Unspecified kind — it is a local wall-clock time, NOT already-UTC), then convert to UTC.
        DateTime fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(from, DateTimeKind.Unspecified), tz);

        // Exclusive upper bound = the start of the day AFTER the local to-date, in UTC.
        DateTime toUtcExclusive = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(to.AddDays(1), DateTimeKind.Unspecified), tz);

        return (fromUtc, toUtcExclusive);
    }

    /// <summary>
    /// Single-day convenience: the half-open UTC window covering the whole local calendar day
    /// <paramref name="localDate"/>. Equivalent to <see cref="ToUtcWindow"/> with equal bounds.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) ForLocalDate(DateTime localDate, TimeZoneInfo tz)
        => ToUtcWindow(localDate, localDate, tz);
}
