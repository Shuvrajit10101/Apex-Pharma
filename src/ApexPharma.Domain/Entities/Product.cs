using System.ComponentModel.DataAnnotations.Schema;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A sellable medicine/item in the catalog (plan.md §7.2). Deliberately holds no
/// quantity: stock lives on <see cref="Batch"/>, because the same product arrives
/// in batches with different expiry dates and MRPs — the single biggest upgrade
/// over the studied source systems (plan.md §7).
/// </summary>
public class Product
{
    public int ProductId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Generic/salt name — lets staff find alternatives when a brand is out of stock.</summary>
    public string? GenericName { get; set; }

    public int ManufacturerId { get; set; }
    public Manufacturer? Manufacturer { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>HSN code — legally required on the GST invoice and for the GSTR-1/HSN summary.</summary>
    public string? HsnCode { get; set; }

    /// <summary>Default GST rate percent for this product (e.g. 5, 12, 18); drives CGST/SGST at billing.</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal GstRate { get; set; }

    /// <summary>Drug schedule; when H/H1/X the billing screen enforces doctor + Rx capture (plan.md §14).</summary>
    public DrugSchedule Schedule { get; set; } = DrugSchedule.None;

    public string? DosageForm { get; set; }
    public string? Strength { get; set; }
    public string? PackSize { get; set; }
    public string? Unit { get; set; }

    /// <summary>Physical rack/shelf location — speeds up picking at the counter.</summary>
    public string? RackLocation { get; set; }

    /// <summary>On-hand at or below this level raises a low-stock/reorder alert (plan.md §6.1).</summary>
    public int ReorderLevel { get; set; }

    /// <summary>Scanned barcode; indexed for &lt;300 ms add-to-bill lookup (plan.md §6.2).</summary>
    public string? Barcode { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<Batch> Batches { get; set; } = new List<Batch>();
    public ICollection<PurchaseItem> PurchaseItems { get; set; } = new List<PurchaseItem>();
    public ICollection<SaleItem> SaleItems { get; set; } = new List<SaleItem>();
    public ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();
}
