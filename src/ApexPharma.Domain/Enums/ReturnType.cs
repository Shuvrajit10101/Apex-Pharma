namespace ApexPharma.Domain.Enums;

/// <summary>
/// Distinguishes the two directions a return can flow (plan.md §6.1 returns).
/// A sale return restocks a batch (customer → us); a purchase return decrements
/// it (us → supplier).
/// </summary>
public enum ReturnType
{
    /// <summary>Customer returns previously sold goods; stock is added back to the batch.</summary>
    Sale = 0,

    /// <summary>We return goods to the supplier; stock is removed from the batch.</summary>
    Purchase = 1
}
