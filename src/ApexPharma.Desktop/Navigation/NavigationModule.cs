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

    /// <summary>Purchases / GRN (placeholder until Phase 1c) — plan.md §6.1.</summary>
    Purchases,

    /// <summary>Reports hub (placeholder until its phase) — plan.md §11.</summary>
    Reports,

    /// <summary>Settings (placeholder until its phase) — plan.md §6.1.</summary>
    Settings
}
