namespace ApexPharma.Desktop.Navigation;

/// <summary>
/// The navigable modules of the single-window shell (plan.md §10). Each left-nav item
/// maps to one of these; the <see cref="INavigationService"/> resolves the matching
/// view-model into the content region. Only <see cref="Masters"/> is implemented today —
/// the rest render a "coming in a later phase" placeholder so no nav button is dead.
/// </summary>
public enum NavigationModule
{
    /// <summary>The default post-login landing/dashboard placeholder ("Select a module to begin").</summary>
    Landing,

    /// <summary>Master data (products, categories, manufacturers, suppliers) — plan.md §6.1.</summary>
    Masters,

    /// <summary>POS billing (placeholder until Phase 1d) — plan.md §6.1.</summary>
    Billing,

    /// <summary>Inventory / stock view (placeholder until its phase) — plan.md §6.1.</summary>
    Inventory,

    /// <summary>Stock adjustments — breakage/count correction + expiry write-off + history (plan.md §6.1).</summary>
    StockAdjustments,

    /// <summary>Purchases / GRN (placeholder until Phase 1c) — plan.md §6.1.</summary>
    Purchases,

    /// <summary>Sales return — reverse items from a completed bill (restock + refund/credit note), plan.md §6.1.</summary>
    SalesReturn,

    /// <summary>Purchase return — send items back to a supplier against a purchase, plan.md §6.1.</summary>
    PurchaseReturn,

    /// <summary>Reports hub (sales/profit, low-stock, expiry, Schedule-H register, GST/HSN) — plan.md §11, §14.</summary>
    Reports,

    /// <summary>Customer khata ledger — receipts + running-balance statement per customer (plan.md §3, §11).</summary>
    CustomerLedger,

    /// <summary>Supplier account ledger — payments + running-balance statement per supplier (plan.md §3, §11).</summary>
    SupplierLedger,

    /// <summary>Day-End — cash reconciliation + the Cashier's own-day view (plan.md §3, §11).</summary>
    DayEnd,

    /// <summary>Settings (placeholder until its phase) — plan.md §6.1.</summary>
    Settings
}
