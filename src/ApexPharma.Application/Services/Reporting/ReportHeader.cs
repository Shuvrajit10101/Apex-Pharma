namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// Minimal pharmacy header for printed reports (plan.md §14 — name + GSTIN/DL on legal
/// documents). Kept separate from the invoice model so the reporting layer doesn't depend on
/// the invoicing types; the caller builds it from the pharmacy profile.
/// </summary>
public sealed class ReportHeader
{
    public string PharmacyName { get; init; } = "Apex-Pharma";

    /// <summary>A one-line sub-header, e.g. "GSTIN: 22AAAAA0000A1Z5 · D.L. No: 20B/21B".</summary>
    public string? SubHeader { get; init; }
}
