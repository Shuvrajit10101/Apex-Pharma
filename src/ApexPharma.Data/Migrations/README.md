# EF Core Migrations

This folder will hold the generated EF Core migrations for the SQLite database.

**No migration has been generated yet** — the .NET 8 SDK was not installed when the
solution was scaffolded (Phase 0). Generate the first migration once the SDK is
present.

## First-time commands (run from the repository root)

```bash
# 1) Install the EF Core CLI tool (once per machine)
dotnet tool install --global dotnet-ef

# 2) Create the initial migration
#    -p = the project that owns the DbContext/migrations (Data)
#    -s = the startup project used to build the app (Desktop)
dotnet ef migrations add InitialCreate -p src/ApexPharma.Data -s src/ApexPharma.Desktop

# 3) Apply it to the local database
dotnet ef database update -p src/ApexPharma.Data -s src/ApexPharma.Desktop
```

The design-time context comes from `ApexPharmaDbContextFactory`
(`Data Source=apexpharma.db`), so the tools can build a context without the full app
host.

## Adding future migrations

```bash
dotnet ef migrations add <Name> -p src/ApexPharma.Data -s src/ApexPharma.Desktop
```

Migrations are applied automatically and safely on app upgrade (plan.md §13).
