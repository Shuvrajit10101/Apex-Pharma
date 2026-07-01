namespace ApexPharma.Domain.Enums;

/// <summary>
/// How a GST invoice's grand total is rounded to a clean, cash-friendly figure (plan.md §6.1
/// tax rounding). The difference is carried on the bill's round-off line so the printed total
/// always reconciles to the line items. The billing engine currently rounds to the nearest
/// whole rupee; this setting captures the pharmacy's preference so the choice is explicit and
/// future rounding strategies (e.g. no rounding) can be honoured without a schema change.
/// </summary>
public enum TaxRoundingMode
{
    /// <summary>Round the grand total to the nearest whole rupee (the common Indian retail default).</summary>
    NearestRupee = 0,

    /// <summary>No rounding — the total is the exact taxable + GST sum (paise shown).</summary>
    None = 1
}
