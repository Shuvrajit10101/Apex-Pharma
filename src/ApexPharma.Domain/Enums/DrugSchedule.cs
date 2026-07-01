namespace ApexPharma.Domain.Enums;

/// <summary>
/// Indian drug schedule classification (Drugs and Cosmetics Rules). Controls the
/// legal compliance path at billing: Schedule H and H1 drugs require the
/// prescriber (doctor) and prescription reference to be captured, and H1 sales
/// must appear in the retail H1 register (plan.md §6.1 billing, §14).
/// </summary>
public enum DrugSchedule
{
    /// <summary>Not a scheduled drug — sold without prescription capture.</summary>
    None = 0,

    /// <summary>Schedule H — prescription-only; capture doctor + Rx reference.</summary>
    H = 1,

    /// <summary>Schedule H1 — prescription-only with mandatory register entry (antibiotics, habit-forming drugs).</summary>
    H1 = 2,

    /// <summary>Schedule X — narcotic/psychotropic; strictest controls (dual prescription, retained copy).</summary>
    X = 3
}
