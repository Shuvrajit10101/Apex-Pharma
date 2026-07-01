namespace ApexPharma.Domain.Entities;

/// <summary>
/// A single key/value application setting (plan.md §7.2, §6.1): pharmacy profile,
/// GSTIN, DL number(s), invoice header/footer, tax rounding, expiry-alert window.
/// A generic key/value store is used so new settings can be added without schema
/// changes; <see cref="Key"/> is the primary key.
/// </summary>
public class Setting
{
    /// <summary>Setting name (primary key), e.g. "Pharmacy.Gstin", "Alert.ExpiryDays".</summary>
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }
}
