# Apex-Pharma

Offline-first Windows desktop **pharmacy management system** for a single Indian retail pharmacy
(GST · Drug License · Schedule H/H1). Fast, keyboard-first POS billing; batch + expiry tracking
with FEFO; GST-compliant invoices; role-based access; automatic local + optional cloud backup.

> This repository is governed by [`plan.md`](plan.md) (the canonical source of truth),
> [`agents.md`](agents.md) (the team roster), and [`memory.md`](memory.md) (living project state).
> Read [`CLAUDE.md`](CLAUDE.md) first every session.

---

## Tech stack

| Concern            | Choice |
|--------------------|--------|
| Language / runtime | C# · **.NET 10** |
| UI                 | **WPF** (MVVM) |
| Database           | **SQLite** via **EF Core** (migrations) |
| Invoices/reports   | QuestPDF *(planned)* |
| Tests              | **xUnit** |
| CI                 | GitHub Actions (Windows) |
| Packaging          | Inno Setup / MSIX *(planned)* |

See [`plan.md` §8](plan.md) for the full stack and rationale.

---

## Prerequisites

- **Windows 10/11** (WPF is Windows-only).
- **.NET 10 SDK** (`10.0.x`) — <https://dotnet.microsoft.com/download/dotnet/10.0>.
  The SDK version is pinned in [`global.json`](global.json).

Verify with:

```bash
dotnet --version   # should report 10.0.x
```

---

## Build, run & test

Once the .NET 10 SDK is installed, from the repository root:

```bash
# Restore NuGet packages for the whole solution
dotnet restore

# Build in Release
dotnet build -c Release

# Run the unit tests (GST math, etc.)
dotnet test

# Launch the desktop app
dotnet run --project src/ApexPharma.Desktop
```

### First-time database setup (EF Core migrations)

The `InitialCreate` migration (regenerated under EF Core 10) ships in the repo. The
running app applies migrations automatically on launch. To create/refresh the local
database manually:

```bash
dotnet tool install --global dotnet-ef
dotnet ef database update -p src/ApexPharma.Data -s src/ApexPharma.Data
```

See [`src/ApexPharma.Data/Migrations/README.md`](src/ApexPharma.Data/Migrations/README.md).

---

## Solution structure

```
ApexPharma.sln
├─ src/
│  ├─ ApexPharma.Domain/        # Entities + enums (no dependencies)
│  ├─ ApexPharma.Data/          # EF Core DbContext, repositories, migrations
│  ├─ ApexPharma.Application/   # Business services (Billing, Inventory, Gst, ...)
│  └─ ApexPharma.Desktop/       # WPF (MVVM) presentation — the shipping app
└─ tests/
   └─ ApexPharma.Tests/         # xUnit tests (money/stock/compliance logic)
```

**Layering** (see [`plan.md` §8](plan.md)): Presentation (WPF/MVVM) → Business (Services) →
Data (Repositories/EF Core) → SQLite. Money and stock rules never live in the UI.

---

## Documentation map

| Need | File |
|------|------|
| **WHAT** we build (canonical plan) | [`plan.md`](plan.md) |
| **WHO** does the work (agent roster) | [`agents.md`](agents.md) |
| **WHERE** we are (living state) | [`memory.md`](memory.md) |
| **RULES** (operating contract) | [`CLAUDE.md`](CLAUDE.md) |

---

## Status

**Phase 0 — Setup.** Solution skeleton scaffolded (this commit): layered projects, domain
entities, EF Core `DbContext` + repositories, service interfaces, a fully-implemented
`GstService` with tests, a minimal WPF MVVM shell, and CI. The EF `InitialCreate` migration
exists (regenerated under EF Core 10); Phase 1 feature work is next — see [`plan.md` §15](plan.md).
