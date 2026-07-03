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

    public FakeTimeZoneProvider(TimeZoneInfo tz) => _tz = tz;

    public TimeZoneInfo GetPharmacyTimeZone() => _tz;
}

/// <summary>Shared timezones for tests, resolved once and independent of the host machine.</summary>
public static class TestTz
{
    /// <summary>
    /// India Standard Time (UTC+5:30, no DST). Resolves the Windows id, then the IANA id, and
    /// finally a hand-built fixed +05:30 zone, so it works on Windows, Linux, and CI alike.
    /// </summary>
    public static readonly TimeZoneInfo Ist = ResolveIst();

    /// <summary>A test provider fixed to <see cref="Ist"/>.</summary>
    public static FakeTimeZoneProvider IstProvider() => new(Ist);

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
