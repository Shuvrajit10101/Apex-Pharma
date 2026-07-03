using System;
using ApexPharma.Application.Time;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Unit tests for <see cref="DayWindow"/> and <see cref="SettingsTimeZoneProvider"/> (Phase 2g1,
/// plan.md §11). Every case passes an EXPLICIT <see cref="TimeZoneInfo"/> so the assertions are
/// independent of the host machine's timezone — the whole point of the helper is that the day
/// boundary is decided by the pharmacy timezone, never the machine clock. IST facts used throughout:
/// UTC+5:30, no DST, so local day D maps to the half-open UTC window <c>[D-1 18:30Z, D 18:30Z)</c>.
/// </summary>
public class DayWindowTests
{
    private static readonly TimeZoneInfo Ist = TestTz.Ist;

    private static DateTime Utc(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void ForLocalDate_Ist_MapsToHalfOpenUtcWindow_Minus0530()
    {
        // Local day 2026-06-15 → [2026-06-14 18:30Z, 2026-06-15 18:30Z).
        var (fromUtc, toUtcExclusive) = DayWindow.ForLocalDate(new DateTime(2026, 6, 15), Ist);

        Assert.Equal(Utc(2026, 6, 14, 18, 30), fromUtc);
        Assert.Equal(Utc(2026, 6, 15, 18, 30), toUtcExclusive);
        Assert.Equal(DateTimeKind.Utc, fromUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, toUtcExclusive.Kind);
    }

    [Fact]
    public void ForLocalDate_Ist_InstantAtLocalMidnightPlus5Min_IsInWindow()
    {
        var (fromUtc, toUtcExclusive) = DayWindow.ForLocalDate(new DateTime(2026, 6, 15), Ist);

        // IST 2026-06-15 00:05 == 2026-06-14 18:35Z → inside [from, toExclusive).
        DateTime instant = Utc(2026, 6, 14, 18, 35);
        Assert.True(instant >= fromUtc && instant < toUtcExclusive);
    }

    [Fact]
    public void ForLocalDate_Ist_InstantAtLocalDayEnd_2355_IsInWindow()
    {
        var (fromUtc, toUtcExclusive) = DayWindow.ForLocalDate(new DateTime(2026, 6, 15), Ist);

        // IST 2026-06-15 23:55 == 2026-06-15 18:25Z → still inside.
        DateTime instant = Utc(2026, 6, 15, 18, 25);
        Assert.True(instant >= fromUtc && instant < toUtcExclusive);
    }

    [Fact]
    public void ForLocalDate_Ist_InstantAtNextLocalMidnightPlus5Min_IsNotInWindow()
    {
        var (_, toUtcExclusive) = DayWindow.ForLocalDate(new DateTime(2026, 6, 15), Ist);

        // IST 2026-06-16 00:05 == 2026-06-15 18:35Z → at/after the exclusive upper bound → OUT.
        DateTime instant = Utc(2026, 6, 15, 18, 35);
        Assert.False(instant < toUtcExclusive);
    }

    [Fact]
    public void ToUtcWindow_ReversedRange_IsSwapped()
    {
        // from > to → the helper swaps so the window is never empty.
        var (fromUtc, toUtcExclusive) =
            DayWindow.ToUtcWindow(new DateTime(2026, 6, 20), new DateTime(2026, 6, 15), Ist);

        Assert.Equal(Utc(2026, 6, 14, 18, 30), fromUtc);        // start of 06-15
        Assert.Equal(Utc(2026, 6, 20, 18, 30), toUtcExclusive); // end of 06-20 (exclusive)
        Assert.True(fromUtc < toUtcExclusive);
    }

    [Fact]
    public void ToUtcWindow_MultiDaySpan_IsInclusiveOfBothLocalEndDays()
    {
        // Local 06-15..06-17 inclusive → [06-14 18:30Z, 06-17 18:30Z): 3 local days = 72h.
        var (fromUtc, toUtcExclusive) =
            DayWindow.ToUtcWindow(new DateTime(2026, 6, 15), new DateTime(2026, 6, 17), Ist);

        Assert.Equal(Utc(2026, 6, 14, 18, 30), fromUtc);
        Assert.Equal(Utc(2026, 6, 17, 18, 30), toUtcExclusive);
        Assert.Equal(TimeSpan.FromHours(72), toUtcExclusive - fromUtc);

        // An instant on the last local day (06-17 12:00 IST == 06:30Z) is included.
        DateTime lastDayInstant = Utc(2026, 6, 17, 6, 30);
        Assert.True(lastDayInstant >= fromUtc && lastDayInstant < toUtcExclusive);
    }

    [Fact]
    public void ToUtcWindow_IgnoresTimeOfDayAndKind_OnInputs()
    {
        // Inputs are calendar dates: a mid-afternoon local kind and a UTC-kind time both floor to .Date.
        var (fromUtc, toUtcExclusive) = DayWindow.ToUtcWindow(
            new DateTime(2026, 6, 15, 14, 22, 9, DateTimeKind.Local),
            new DateTime(2026, 6, 15, 3, 0, 0, DateTimeKind.Utc),
            Ist);

        Assert.Equal(Utc(2026, 6, 14, 18, 30), fromUtc);
        Assert.Equal(Utc(2026, 6, 15, 18, 30), toUtcExclusive);
    }

    [Fact]
    public void ForLocalDate_Utc_IsAPlainCalendarDay()
    {
        // With a UTC timezone, local day D is simply [D 00:00Z, D+1 00:00Z).
        var (fromUtc, toUtcExclusive) =
            DayWindow.ForLocalDate(new DateTime(2026, 6, 15), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 6, 15, 0, 0), fromUtc);
        Assert.Equal(Utc(2026, 6, 16, 0, 0), toUtcExclusive);
    }

    // ---- Provider fallback ----

    [Fact]
    public void SettingsTimeZoneProvider_UnknownId_FallsBackToIst()
    {
        // A bogus configured id must never throw — it falls back to a +05:30 zone.
        var provider = new SettingsTimeZoneProvider(new StubSettings("Totally/Bogus_Zone"));
        TimeZoneInfo tz = provider.GetPharmacyTimeZone();

        Assert.Equal(TimeSpan.FromMinutes(330), tz.GetUtcOffset(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc)));
        // And it maps a local day exactly as IST does.
        var (fromUtc, _) = DayWindow.ForLocalDate(new DateTime(2026, 6, 15), tz);
        Assert.Equal(Utc(2026, 6, 14, 18, 30), fromUtc);
    }

    [Fact]
    public void SettingsTimeZoneProvider_DefaultIstId_ResolvesToPlus0530()
    {
        var provider = new SettingsTimeZoneProvider(new StubSettings("India Standard Time"));
        TimeZoneInfo tz = provider.GetPharmacyTimeZone();
        Assert.Equal(TimeSpan.FromMinutes(330), tz.GetUtcOffset(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Minimal <see cref="ApexPharma.Application.Services.Settings.ISettingsService"/> stub returning
    /// a fixed timezone id for <c>GetStringAsync</c>; all other members are unused by the provider.
    /// </summary>
    private sealed class StubSettings : ApexPharma.Application.Services.Settings.ISettingsService
    {
        private readonly string _id;
        public StubSettings(string id) => _id = id;

        public Task<string> GetStringAsync(string key, string fallback = "", System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(_id);

        public Task SeedDefaultsAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string? value, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ApexPharma.Application.Services.Settings.PharmacyProfile> GetProfileAsync(System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(new ApexPharma.Application.Services.Settings.PharmacyProfile());
        public Task<ApexPharma.Application.Services.MasterData.MasterResult> SaveProfileAsync(
            ApexPharma.Application.Services.Settings.PharmacyProfile profile, ApexPharma.Domain.Enums.UserRole actingRole, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(ApexPharma.Application.Services.MasterData.MasterResult.Ok());
    }
}
