namespace ApexPharma.Domain.Enums;

/// <summary>
/// Discrete, checkable capabilities used for role-based access control (plan.md §4).
/// A <see cref="UserRole"/> is granted a set of these; the UI and services gate
/// sensitive actions on <c>IAuthService.HasPermission</c> rather than on the role
/// name directly, so the permission surface can evolve without rewriting call sites.
/// </summary>
public enum Permission
{
    /// <summary>Create/edit/deactivate users and assign roles (Owner only, plan.md §4).</summary>
    ManageUsers,

    /// <summary>Add/edit/deactivate products, categories, manufacturers.</summary>
    ManageProducts,

    /// <summary>Add/edit/deactivate suppliers (master data). Owner + Pharmacist, plan.md §4/§6.1.</summary>
    ManageSuppliers,

    /// <summary>Edit sale prices / margins (Owner only — Pharmacist and Cashier cannot, plan.md §4).</summary>
    EditPrices,

    /// <summary>Create sales / operate the POS billing screen.</summary>
    DoBilling,

    /// <summary>Record purchases / GRN from suppliers (stock in).</summary>
    DoPurchases,

    /// <summary>Stock adjustments: expiry write-off, breakage, physical-count correction.</summary>
    AdjustStock,

    /// <summary>Process sales returns and purchase returns.</summary>
    DoReturns,

    /// <summary>View the full report suite (sales, profit, stock, expiry, registers).</summary>
    ViewReports,

    /// <summary>View current stock and price without editing (Cashier can, plan.md §4).</summary>
    ViewStock,

    /// <summary>View / run the day-end summary and cash reconciliation.</summary>
    DayEnd,

    /// <summary>Change pharmacy settings: profile, GSTIN, DL number, tax, alert windows (Owner only).</summary>
    ManageSettings,

    /// <summary>Run and restore backups.</summary>
    Backup
}
