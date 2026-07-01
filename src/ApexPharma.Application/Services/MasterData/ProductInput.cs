using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Data carried into <see cref="IProductService"/> create/update (plan.md §7.2 product
/// fields). Stock/price live on <see cref="Domain.Entities.Batch"/>, so this catalog DTO
/// deliberately carries no quantity — only the master attributes (plan.md §7).
/// </summary>
public sealed class ProductInput
{
    public string Name { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public int ManufacturerId { get; set; }
    public int CategoryId { get; set; }
    public string? HsnCode { get; set; }
    public decimal GstRate { get; set; }
    public DrugSchedule Schedule { get; set; } = DrugSchedule.None;
    public string? DosageForm { get; set; }
    public string? Strength { get; set; }
    public string? PackSize { get; set; }
    public string? Unit { get; set; }
    public string? RackLocation { get; set; }
    public int ReorderLevel { get; set; }
    public string? Barcode { get; set; }
}
