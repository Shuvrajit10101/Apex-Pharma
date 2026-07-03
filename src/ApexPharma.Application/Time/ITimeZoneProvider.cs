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
}
