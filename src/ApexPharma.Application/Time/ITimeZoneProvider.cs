namespace ApexPharma.Application.Time;

/// <summary>
/// Supplies the pharmacy's operating timezone — the timezone the operator's date pickers are read
/// in — so <see cref="DayWindow"/> can map local calendar days to UTC instant windows. A single
/// seam over the configured <c>Pharmacy.TimeZone</c> setting, injected into every service that
/// builds a date window (reports, ledgers, day-end) so the day boundary is consistent and testable
/// (a test can inject an explicit timezone independent of the host machine).
/// </summary>
public interface ITimeZoneProvider
{
    /// <summary>
    /// The pharmacy's timezone (e.g. India Standard Time). Never throws and never returns null —
    /// an unresolvable configured id falls back to a hardcoded IST zone.
    /// </summary>
    TimeZoneInfo GetPharmacyTimeZone();

    /// <summary>
    /// The pharmacy-local calendar date "now" — i.e. the trading day. Every "expired as of today"
    /// judgment (FEFO dispense, expiry write-off, the expired/inventory screens) must derive its
    /// "today" from this, so a batch expiring on the current IST date is treated identically across
    /// the whole app even during the IST 00:00–05:30 window when UTC is still the prior day
    /// (plan.md §11, §14). Backed by converting "now" (UTC) into the pharmacy zone and taking the date.
    /// </summary>
    DateTime LocalToday();

    /// <summary>
    /// Projects a stored UTC instant into the pharmacy-local wall-clock time (for display, e.g. the
    /// printed invoice bill date). The input is treated as UTC regardless of its
    /// <see cref="DateTimeKind"/>; the result is the local date/time in the pharmacy timezone.
    /// </summary>
    DateTime ToLocal(DateTime utcInstant);
}
