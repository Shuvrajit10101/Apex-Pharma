namespace ApexPharma.Domain.Enums;

/// <summary>
/// Reason a batch's on-hand quantity was changed outside of a sale or purchase
/// (plan.md §6.1 inventory operations). Every adjustment is audited.
/// </summary>
public enum AdjustmentType
{
    /// <summary>Expired stock written off (removed from sellable inventory).</summary>
    Expiry = 0,

    /// <summary>Breakage / wastage / damage.</summary>
    Breakage = 1,

    /// <summary>Physical-count correction — reconciling recorded qty to a shelf count.</summary>
    CountCorrection = 2
}
