using System.Text.RegularExpressions;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Validates the 15-character Indian GSTIN format (plan.md §6.1, §14 compliance).
/// Structure: 2-digit state code, 10-char PAN (5 letters, 4 digits, 1 letter),
/// 1 entity digit, the literal 'Z', and 1 checksum char. This is a <b>format</b>
/// check (shape + character classes), not a checksum verification — sufficient to
/// reject typos while leaving the strict modulus check to a later pass.
/// </summary>
public static partial class GstinValidator
{
    // 2 (state) + 5 letters + 4 digits + 1 letter (PAN) + 1 (entity) + Z + 1 (check).
    [GeneratedRegex("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z]{1}[0-9A-Z]{1}Z[0-9A-Z]{1}$")]
    private static partial Regex GstinPattern();

    /// <summary>True when <paramref name="gstin"/> matches the 15-char GSTIN format.</summary>
    public static bool IsValid(string gstin)
        => !string.IsNullOrWhiteSpace(gstin) && GstinPattern().IsMatch(gstin.Trim().ToUpperInvariant());
}
