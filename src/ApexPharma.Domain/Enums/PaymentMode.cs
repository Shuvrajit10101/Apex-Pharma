namespace ApexPharma.Domain.Enums;

/// <summary>
/// How a sale was settled at the counter (plan.md §6.1 billing). Credit means the
/// amount is added to the customer's outstanding balance rather than collected now.
/// </summary>
public enum PaymentMode
{
    Cash = 0,
    Upi = 1,
    Card = 2,

    /// <summary>Billed to the customer's khata (outstanding balance); no cash collected at sale time.</summary>
    Credit = 3
}
