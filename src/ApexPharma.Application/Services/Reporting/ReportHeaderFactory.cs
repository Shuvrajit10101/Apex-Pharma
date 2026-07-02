using System.Collections.Generic;
using ApexPharma.Application.Services.Settings;

namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// Builds a <see cref="ReportHeader"/> from a <see cref="PharmacyProfile"/> for printed reports
/// and ledger statements. Single source of truth for the name-fallback and the GSTIN/D.L.
/// sub-header composition (plan.md §14), so every export renders a byte-identical header.
/// </summary>
public static class ReportHeaderFactory
{
    /// <summary>
    /// Composes a header from the pharmacy profile: <see cref="ReportHeader.PharmacyName"/> falls
    /// back to "Apex-Pharma" when blank, and the sub-header lists GSTIN and D.L. number when present.
    /// </summary>
    public static ReportHeader From(PharmacyProfile profile) => new()
    {
        PharmacyName = string.IsNullOrWhiteSpace(profile.PharmacyName) ? "Apex-Pharma" : profile.PharmacyName,
        SubHeader = BuildSubHeader(profile),
    };

    private static string? BuildSubHeader(PharmacyProfile profile)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.Gstin))
        {
            parts.Add($"GSTIN: {profile.Gstin}");
        }

        if (!string.IsNullOrWhiteSpace(profile.DlNumber))
        {
            parts.Add($"D.L. No: {profile.DlNumber}");
        }

        return parts.Count == 0 ? null : string.Join("  ·  ", parts);
    }
}
