using ApexPharma.Application.Services.Settings;

namespace ApexPharma.Application.Time;

/// <summary>
/// <see cref="ITimeZoneProvider"/> backed by the <c>Pharmacy.TimeZone</c> setting
/// (default <c>"India Standard Time"</c>). Resolves the configured id via
/// <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/> and caches the resolved
/// <see cref="TimeZoneInfo"/> after the first read (the pharmacy timezone never changes mid-session).
/// <para>
/// <b>Never throws.</b> If the configured id is unknown on this host (a bad value, or the OS uses
/// the IANA id on Linux/CI rather than the Windows id), it falls back — trying the Windows id, then
/// the IANA id, then a hardcoded fixed +05:30 zone — so a window is always built on IST rather than
/// failing. Windows and most modern .NET runtimes accept BOTH the Windows ("India Standard Time")
/// and IANA ("Asia/Kolkata") ids, so the default resolves on either.
/// </para>
/// </summary>
public sealed class SettingsTimeZoneProvider : ITimeZoneProvider
{
    /// <summary>The setting key holding the pharmacy timezone id.</summary>
    public const string TimeZoneSettingKey = "Pharmacy.TimeZone";

    /// <summary>The default timezone id (Windows id; also seeded in <c>SettingsService.Defaults</c>).</summary>
    public const string DefaultTimeZoneId = "India Standard Time";

    private readonly ISettingsService _settings;
    private readonly object _gate = new();
    private TimeZoneInfo? _cached;

    public SettingsTimeZoneProvider(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public TimeZoneInfo GetPharmacyTimeZone()
    {
        // Cache the resolved zone after the first read — it does not change during a session, and
        // this keeps the (synchronous) hot path off the settings store on every window build.
        if (_cached is not null)
        {
            return _cached;
        }

        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            string id = ReadConfiguredId();
            _cached = Resolve(id);
            return _cached;
        }
    }

    /// <inheritdoc />
    public DateTime LocalToday() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GetPharmacyTimeZone()).Date;

    /// <inheritdoc />
    public DateTime ToLocal(DateTime utcInstant) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc), GetPharmacyTimeZone());

    /// <summary>
    /// Reads the configured id from the settings store, defaulting to <see cref="DefaultTimeZoneId"/>.
    /// The provider is a synchronous seam over the async settings API; the value is read once and
    /// cached, so blocking here is a single, bounded call (never on a UI-critical hot loop).
    /// </summary>
    private string ReadConfiguredId()
    {
        try
        {
            string id = _settings
                .GetStringAsync(TimeZoneSettingKey, DefaultTimeZoneId)
                .GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(id) ? DefaultTimeZoneId : id.Trim();
        }
        catch
        {
            // A settings read failure must never break windowing — fall back to the default id.
            return DefaultTimeZoneId;
        }
    }

    /// <summary>
    /// Resolves an id to a <see cref="TimeZoneInfo"/>, falling back to IST on any failure. Tries the
    /// configured id, then the Windows IST id, then the IANA IST id, and finally a hand-built fixed
    /// +05:30 zone so the result is guaranteed non-null and never throws.
    /// </summary>
    private static TimeZoneInfo Resolve(string id)
    {
        if (TryFind(id, out TimeZoneInfo? tz))
        {
            return tz!;
        }

        // Fallback chain: Windows id → IANA id → fixed custom +05:30 zone.
        if (TryFind("India Standard Time", out tz) || TryFind("Asia/Kolkata", out tz))
        {
            return tz!;
        }

        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
    }

    private static bool TryFind(string id, out TimeZoneInfo? tz)
    {
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            tz = null;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            tz = null;
            return false;
        }
    }
}
