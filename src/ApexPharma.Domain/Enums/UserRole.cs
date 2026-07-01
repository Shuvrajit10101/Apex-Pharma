namespace ApexPharma.Domain.Enums;

/// <summary>
/// Role-based access control tiers (plan.md §4). Determines which screens and
/// operations a signed-in user may perform.
/// </summary>
public enum UserRole
{
    /// <summary>Full access: masters, purchases, billing, returns, price/stock edits, reports, users, settings, backup.</summary>
    Owner = 0,

    /// <summary>Billing (incl. Schedule H/H1), purchases/GRN, stock adjustments, returns, view reports. Cannot edit prices/margins, manage users, or change settings.</summary>
    Pharmacist = 1,

    /// <summary>Billing, view stock/price, day-end summary only. Cannot purchase, edit products/prices, adjust stock, or reach owner reports/settings.</summary>
    Cashier = 2
}
