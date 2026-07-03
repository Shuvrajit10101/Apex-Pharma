using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Settings;

/// <summary>
/// The pharmacy profile (plan.md §6.1 Settings, §14 compliance) — a strongly-typed view over the
/// key/value <see cref="Domain.Entities.Setting"/> store. Carries everything the GST-compliant
/// invoice needs in its header (name, address, <b>GSTIN</b>, <b>DL number</b>, phone) plus the
/// invoice footer/terms and the operational knobs (near-expiry window, tax rounding mode).
/// A plain DTO so the presentation layer never reads raw settings keys and the invoice service
/// gets one place to pull compliant header data from.
/// </summary>
public sealed class PharmacyProfile
{
    /// <summary>Registered pharmacy/store name — the invoice header title.</summary>
    public string PharmacyName { get; set; } = string.Empty;

    /// <summary>Street/building address line printed under the name.</summary>
    public string AddressLine { get; set; } = string.Empty;

    /// <summary>City for the address block.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>State for the address block (also the GST place-of-supply state).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Pharmacy GSTIN — legally required on every GST invoice (plan.md §14).</summary>
    public string Gstin { get; set; } = string.Empty;

    /// <summary>Retail drug-license number (Form 20/21) — legally required on every bill (plan.md §14).</summary>
    public string DlNumber { get; set; } = string.Empty;

    /// <summary>Contact phone shown in the header.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>
    /// Pharmacy operating timezone id (e.g. "India Standard Time"). Drives the local→UTC
    /// day-boundary window across reports, ledgers, and day-end (plan.md §11). Defaults to IST;
    /// no Settings-UI field this phase.
    /// </summary>
    public string TimeZone { get; set; } = "India Standard Time";

    /// <summary>Free-text footer / terms &amp; conditions printed at the bottom of the receipt.</summary>
    public string InvoiceFooter { get; set; } = string.Empty;

    /// <summary>Near-expiry alert window in days (plan.md §6.1) — defaults to 90.</summary>
    public int NearExpiryDays { get; set; } = 90;

    /// <summary>How invoice totals are rounded to a clean figure (plan.md §6.1 tax rounding).</summary>
    public TaxRoundingMode TaxRoundingMode { get; set; } = TaxRoundingMode.NearestRupee;
}
