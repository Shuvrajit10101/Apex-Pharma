using System.Globalization;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.Settings;

/// <summary>
/// Concrete pharmacy-profile settings service (plan.md §6.1, §14) over the key/value
/// <see cref="Setting"/> store. Keys live under a <c>Pharmacy.*</c> / <c>Invoice.*</c> / <c>Alert.*</c>
/// namespace so the store stays self-describing. All writes are idempotent upserts; reads fall back
/// to sensible defaults so the invoice header is always populated even before the Owner configures
/// it. Saving the profile is gated on <see cref="Permission.ManageSettings"/> (Owner only, plan.md §4).
/// No money/stock rule here — this is configuration data (plan.md §8 layering).
/// </summary>
public sealed class SettingsService : ISettingsService
{
    // Setting keys — grouped by concern; kept in one place so the profile mapping is the single
    // source of truth for what a key means.
    internal const string KeyPharmacyName = "Pharmacy.Name";
    internal const string KeyAddressLine = "Pharmacy.AddressLine";
    internal const string KeyCity = "Pharmacy.City";
    internal const string KeyState = "Pharmacy.State";
    internal const string KeyGstin = "Pharmacy.Gstin";
    internal const string KeyDlNumber = "Pharmacy.DlNumber";
    internal const string KeyPhone = "Pharmacy.Phone";
    internal const string KeyInvoiceFooter = "Invoice.Footer";
    internal const string KeyNearExpiryDays = "Alert.NearExpiryDays";
    internal const string KeyTaxRoundingMode = "Invoice.TaxRoundingMode";

    /// <summary>The seeded defaults — placeholder-but-valid values so a fresh install prints a
    /// recognisable header the Owner can then correct (plan.md §17.5 GST defaults-now).</summary>
    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        [KeyPharmacyName] = "Apex Pharmacy",
        [KeyAddressLine] = "Shop address line",
        [KeyCity] = "City",
        [KeyState] = "State",
        [KeyGstin] = "",
        [KeyDlNumber] = "",
        [KeyPhone] = "",
        [KeyInvoiceFooter] = "Thank you for your visit. Get well soon!",
        [KeyNearExpiryDays] = "90",
        [KeyTaxRoundingMode] = nameof(TaxRoundingMode.NearestRupee),
    };

    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public SettingsService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task SeedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        List<string> existing = await _db.Settings
            .Where(s => Defaults.Keys.Contains(s.Key))
            .Select(s => s.Key)
            .ToListAsync(cancellationToken);

        var toAdd = Defaults
            .Where(kv => !existing.Contains(kv.Key))
            .Select(kv => new Setting { Key = kv.Key, Value = kv.Value })
            .ToList();

        if (toAdd.Count > 0)
        {
            await _db.Settings.AddRangeAsync(toAdd, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<string> GetStringAsync(string key, string fallback = "", CancellationToken cancellationToken = default)
    {
        Setting? setting = await _db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        return setting?.Value ?? fallback;
    }

    /// <inheritdoc />
    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken cancellationToken = default)
    {
        Setting? setting = await _db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        return setting?.Value is { } value
               && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }

    /// <inheritdoc />
    public async Task SetStringAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        Setting? setting = await _db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            await _db.Settings.AddAsync(new Setting { Key = key, Value = value }, cancellationToken);
        }
        else
        {
            setting.Value = value;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PharmacyProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        // Single read of all relevant keys, then map with per-key fallback so a partially-seeded
        // store still yields a complete, usable profile.
        Dictionary<string, string?> values = await _db.Settings
            .Where(s => Defaults.Keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        string Read(string key) => values.TryGetValue(key, out string? v) && v is not null
            ? v
            : Defaults[key];

        return new PharmacyProfile
        {
            PharmacyName = Read(KeyPharmacyName),
            AddressLine = Read(KeyAddressLine),
            City = Read(KeyCity),
            State = Read(KeyState),
            Gstin = Read(KeyGstin),
            DlNumber = Read(KeyDlNumber),
            Phone = Read(KeyPhone),
            InvoiceFooter = Read(KeyInvoiceFooter),
            NearExpiryDays = int.TryParse(Read(KeyNearExpiryDays), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days > 0
                ? days
                : 90,
            TaxRoundingMode = Enum.TryParse(Read(KeyTaxRoundingMode), out TaxRoundingMode mode)
                ? mode
                : TaxRoundingMode.NearestRupee,
        };
    }

    /// <inheritdoc />
    public async Task<MasterResult> SaveProfileAsync(PharmacyProfile profile, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        // Owner-only: changing GSTIN/DL/tax config is a settings action (plan.md §4).
        if (!_auth.HasPermission(actingRole, Permission.ManageSettings))
        {
            return MasterResult.Fail("You do not have permission to change pharmacy settings.");
        }

        if (profile is null)
        {
            return MasterResult.Fail("Pharmacy profile is required.");
        }

        if (string.IsNullOrWhiteSpace(profile.PharmacyName))
        {
            return MasterResult.Fail("Pharmacy name is required.");
        }

        if (profile.NearExpiryDays <= 0)
        {
            return MasterResult.Fail("Near-expiry days must be greater than zero.");
        }

        // GSTIN, when supplied, must be the 15-char format (blank is allowed pre-configuration so
        // the Owner can fill it in later; the CA reconciles exact values before go-live — plan.md §17.5).
        string gstin = (profile.Gstin ?? string.Empty).Trim();
        if (gstin.Length > 0 && !GstinValidator.IsValid(gstin))
        {
            return MasterResult.Fail("GSTIN must be a valid 15-character GST identification number.");
        }

        var toWrite = new Dictionary<string, string?>
        {
            [KeyPharmacyName] = profile.PharmacyName.Trim(),
            [KeyAddressLine] = (profile.AddressLine ?? string.Empty).Trim(),
            [KeyCity] = (profile.City ?? string.Empty).Trim(),
            [KeyState] = (profile.State ?? string.Empty).Trim(),
            [KeyGstin] = gstin,
            [KeyDlNumber] = (profile.DlNumber ?? string.Empty).Trim(),
            [KeyPhone] = (profile.Phone ?? string.Empty).Trim(),
            [KeyInvoiceFooter] = (profile.InvoiceFooter ?? string.Empty).Trim(),
            [KeyNearExpiryDays] = profile.NearExpiryDays.ToString(CultureInfo.InvariantCulture),
            [KeyTaxRoundingMode] = profile.TaxRoundingMode.ToString(),
        };

        // Upsert all keys in one transaction so the profile saves atomically.
        List<Setting> existing = await _db.Settings
            .Where(s => toWrite.Keys.Contains(s.Key))
            .ToListAsync(cancellationToken);

        foreach ((string key, string? value) in toWrite)
        {
            Setting? row = existing.FirstOrDefault(s => s.Key == key);
            if (row is null)
            {
                await _db.Settings.AddAsync(new Setting { Key = key, Value = value }, cancellationToken);
            }
            else
            {
                row.Value = value;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }
}
