using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Settings;

/// <summary>
/// Typed access to the pharmacy profile over the key/value <see cref="Domain.Entities.Setting"/>
/// store (plan.md §6.1 Settings, §14 compliance). Provides get/set for individual typed values and
/// a strongly-typed <see cref="PharmacyProfile"/> accessor so callers never touch raw keys. Saving
/// the profile is Owner-only (gated on <see cref="Permission.ManageSettings"/>, plan.md §4). Defaults
/// are seeded idempotently so a fresh install already prints a usable (if placeholder) invoice header.
/// </summary>
public interface ISettingsService
{
    /// <summary>Seeds any missing default settings — safe to call on every startup (idempotent).</summary>
    Task SeedDefaultsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a raw string setting, or <paramref name="fallback"/> when absent.</summary>
    Task<string> GetStringAsync(string key, string fallback = "", CancellationToken cancellationToken = default);

    /// <summary>Reads an int setting, or <paramref name="fallback"/> when absent/unparseable.</summary>
    Task<int> GetIntAsync(string key, int fallback, CancellationToken cancellationToken = default);

    /// <summary>Writes (inserts or updates) a raw string setting.</summary>
    Task SetStringAsync(string key, string? value, CancellationToken cancellationToken = default);

    /// <summary>Loads the strongly-typed pharmacy profile (defaults fill any missing keys).</summary>
    Task<PharmacyProfile> GetProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the whole pharmacy profile in one save (Owner only). Returns a failed
    /// <see cref="MasterResult"/> for an unauthorized caller or a validation failure — expected
    /// failures are return values, not exceptions (plan.md §6.2).
    /// </summary>
    Task<MasterResult> SaveProfileAsync(PharmacyProfile profile, UserRole actingRole, CancellationToken cancellationToken = default);
}
