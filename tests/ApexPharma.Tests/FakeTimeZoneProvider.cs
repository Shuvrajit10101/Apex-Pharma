using System;
using ApexPharma.Application.Time;

namespace ApexPharma.Tests;

/// <summary>
/// A test <see cref="ITimeZoneProvider"/> that returns a fixed, explicitly-supplied
/// <see cref="TimeZoneInfo"/>. Services under test take a TZ provider so their date windows are
/// deterministic and independent of the host machine's timezone — every test injects the zone it
/// wants rather than reading the machine-local clock (which made the ledger carry-forward tests
/// flaky near IST midnight on an IST host).
/// </summary>
public sealed class FakeTimeZoneProvider : ITimeZoneProvider
{
    private readonly TimeZoneInfo _tz;
    private readonly DateTime? _fixedUtcNow;

    /// <param name="tz">The pharmacy timezone the provider reports.</param>
    /// <param name="fixedUtcNow">
    /// Optional pinned "now" (interpreted as UTC). When supplied, <see cref="LocalToday"/> derives
    /// from this fixed instant instead of the wall clock, so a test that also seeds data from the
    /// same instant is fully deterministic — no double read of <see cref="DateTime.UtcNow"/> that
    /// could straddle a day boundary between the seed and the service call. When null, the provider
    /// falls back to the real clock.
    /// </param>
    public FakeTimeZoneProvider(TimeZoneInfo tz, DateTime? fixedUtcNow = null)
    {
        _tz = tz;
        _fixedUtcNow = fixedUtcNow is DateTime f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null;
    }

    public TimeZoneInfo GetPharmacyTimeZone() => _tz;

    public DateTime LocalToday() =>
        TimeZoneInfo.ConvertTimeFromUtc(_fixedUtcNow ?? DateTime.UtcNow, _tz).Date;

    public DateTime ToLocal(DateTime utcInstant) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc), _tz);
}

/// <summary>Shared timezones for tests, resolved once and independent of the host machine.</summary>
public static class TestTz
{
    /// <summary>
    /// India Standard Time (UTC+5:30, no DST). Resolves the Windows id, then the IANA id, and
    /// finally a hand-built fixed +05:30 zone, so it works on Windows, Linux, and CI alike.
    /// </summary>
    public static readonly TimeZoneInfo Ist = ResolveIst();

    /// <summary>A test provider fixed to <see cref="Ist"/>, using the real wall clock for "now".</summary>
    public static FakeTimeZoneProvider IstProvider() => new(Ist);

    /// <summary>
    /// A test provider fixed to <see cref="Ist"/> with a PINNED "now" (interpreted as UTC), so
    /// <see cref="ITimeZoneProvider.LocalToday"/> is deterministic. Use this for IST/UTC boundary
    /// tests: seed expiries and drive the service from the SAME instant to remove the clock race.
    /// </summary>
    public static FakeTimeZoneProvider IstProvider(DateTime fixedUtcNow) => new(Ist, fixedUtcNow);

    /// <summary>A test provider fixed to UTC (day windows behave as a pure UTC calendar day).</summary>
    public static FakeTimeZoneProvider UtcProvider() => new(TimeZoneInfo.Utc);

    private static TimeZoneInfo ResolveIst()
    {
        foreach (string id in new[] { "India Standard Time", "Asia/Kolkata" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
    }
}
