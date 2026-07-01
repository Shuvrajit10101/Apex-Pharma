# EF Core Migrations

This folder holds the generated EF Core migrations for the SQLite database.

A single **`InitialCreate`** migration exists — it was regenerated under **EF Core 10**
(during the .NET 10 upgrade) so the whole schema is captured in one migration with a
current `10.x` product version. It reproduces the full current model: all keys,
FK delete behaviours, decimal precision, and every unique/lookup index
(`IX_Users_Username`, NOCASE-unique `Category`/`Manufacturer` names, filtered-unique
`Product.Barcode`, unique `Sale.BillNo`, composite `Batch(ProductId, BatchNo)`, plus the
product-name/barcode and batch-expiry lookup indexes).

The running app applies pending migrations automatically and safely on launch (plan.md §13).

## Commands (run from the repository root)

```bash
# Install / update the EF Core CLI tool (once per machine)
dotnet tool update --global dotnet-ef --version 10.*

# Apply migrations to the local database
#   -p = the project that owns the DbContext/migrations (Data)
#   -s = the startup project (Data — the design-time factory builds the context)
dotnet ef database update -p src/ApexPharma.Data -s src/ApexPharma.Data
```

The design-time context comes from `ApexPharmaDbContextFactory`
(`Data Source=apexpharma.db`), so the tools can build a context without the full app host.

## Adding future migrations

```bash
dotnet ef migrations add <Name> -p src/ApexPharma.Data -s src/ApexPharma.Data
```
