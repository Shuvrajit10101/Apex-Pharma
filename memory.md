# memory.md — Apex-Pharma Living Memory

> **Purpose:** this file fills the gap between sessions. It is the project's working memory — read it at the start of every session and **append to it after every task**. If it isn't written here, it will be forgotten.
>
> **How to maintain (every session, every task):**
> - After each completed task, add a dated bullet under **Session Log** (what changed · decisions · evidence · next step).
> - Log every **minor change/assumption** under **Change & Decision Log**.
> - Keep **Open Questions**, **Current Status**, and **Next Steps** current — edit them in place, don't just append.
> - Keep it tight; move stale detail into `plan.md` or archive older sessions at the bottom.

---

## Project snapshot

- **Project:** Apex-Pharma — offline-first Windows desktop pharmacy management system.
- **Client goal:** production build to ship to a real single retail pharmacy (India).
- **Context:** single store · offline desktop · India (GST, Drug License, Schedule H/H1).
- **Repo:** https://github.com/Shuvrajit10101/Apex-Pharma  *(owned by the GitHub Expert agent)*
- **Canonical plan:** `plan.md`  ·  **Rules:** `CLAUDE.md`  ·  **Team:** `agents.md`
- **Stack (locked):** .NET 8 · C# · WPF (MVVM) · SQLite + EF Core · QuestPDF · xUnit · Git/GitHub · Inno Setup/MSIX.

## Current status

- **Phase:** **Phase 1 (Core MVP) — in progress.** 1(a)/1(b)/nav-shell merged; 1(c) Purchase/GRN done + fixed, merging next.
- **Done:** Phase 0; **1(a)** auth (PR #1); **1(b)** masters (PR #6); **nav-shell** single-window UI (PR #7); **client §17 answers resolved**; **1(c)** Purchase/GRN stock-in (batch+expiry, ACID) + read-only Inventory (near-expiry/low-stock) + returns, `DoPurchases`/`ViewStock` gating — **171 tests**, reviewed (approve-with-nits, fixed). Commit `e7d5dae` on `feature/purchase-grn`.
- **Repo:** live on GitHub, `main` @ `47095fa`; CI green. Branch protection unavailable (free private) → process-enforced.
- **Now:** GitHub Expert to merge `feature/purchase-grn` → `main`.
- **Next:** **.NET 8 → .NET 10 (LTS) upgrade** (owner-approved — all projects/CI/docs/migrations), then **Phase 1(d) POS billing** (GST + Schedule-H + FEFO + thermal receipt + khata).

---

## Session Log

### 2026-07-01 — Session 1 (planning & governance)
- **Built the `/software` skill** (`~/.claude/skills/software/`) from the *Software Development: A Practical Approach!* textbook — the SDLC methodology backbone for this project. Verified (structure + functional smoke test passed).
- **Studied 3 reference reports** (Medical Store "Pharmiz", Hari-Om Medical Store, EM's Pharmacy) via a parallel workflow. Harvested modules, data models, and gaps. Key finding: all three lack batch/expiry tracking, GST, and Schedule-H compliance.
- **Confirmed client choices:** single retail pharmacy · offline-first desktop · India (GST/DL/Schedule H) · real production build to ship.
- **Wrote the canonical plan** → `plan.md` (18 sections: requirements, ER model, architecture, workflows, tests, roadmap, risks).
- **Established governance:** created `CLAUDE.md`, `agents.md`, `memory.md`; consolidated the plan into `plan.md` (removed the interim `PHARMACY-SOFTWARE-PLAN.md`).
- **Next step:** get the §17 client answers, then have the GitHub Expert scaffold the repo and DevOps set up CI (Phase 0).

### 2026-07-01 — Session 1 (cont.) — Phase 0 kickoff
- **Toolchain recon:** git ✅; **no .NET SDK** (only .NET 6 runtime) → need .NET 8 SDK; **gh CLI** not installed. winget/choco available.
- **Toolchain install:** winget installs of .NET 8 SDK + gh failed (exit 1602 — need admin/UAC, unavailable non-interactively). Switched to **no-admin user-local install**: Microsoft `dotnet-install.ps1` → `%LocalAppData%\Microsoft\dotnet`; gh portable zip → `%LocalAppData%\Programs\gh`; both added to **User PATH** + `DOTNET_ROOT` set. *(running)*
- **Scaffolded the .NET 8 solution** (build agent): Domain (17 entities + 5 enums), Data (EF Core SQLite DbContext + repositories + design-time factory), Application (service interfaces + **fully-implemented, tested `GstService`**), Desktop (WPF/MVVM shell), Tests (xUnit GST tests). 67 files → local `main` `eb76f36`. Nothing pushed; PDFs/bin/obj git-ignored.
- **Git identity:** machine git already `Shuvrajit1010 / dkphomechoudhury@gmail.com` (matches repo owner) — good for pushing later.
- **Next:** once SDK ready → `dotnet restore/build/test` to verify scaffold + GST tests; then `dotnet ef migrations add InitialCreate`; then (on your OK) gh auth + push.

### 2026-07-01 — Session 1 (cont.) — Phase 0 verified & closed
- **Toolchain installed** user-local: .NET SDK **8.0.422** at `%LocalAppData%\Microsoft\dotnet`, gh **2.95.0** at `%LocalAppData%\Programs\gh`. Both on User PATH + `DOTNET_ROOT` set. NOTE for future shells: `dotnet-ef` spawns `dotnet` from PATH — prepend `%LocalAppData%\Microsoft\dotnet` so it finds the SDK (the old Program Files .NET 6 has no SDK).
- **Build verified:** `dotnet build -c Release` → 0 warnings / 0 errors across all 5 projects (incl. WPF). `dotnet test` → **9/9 GST tests pass**.
- **Two build fixes:** (1) `App` base fully-qualified to `System.Windows.Application` (CS0118 clash with the `ApexPharma.Application` project namespace); (2) explicit `HasKey` for `AuditLog`/`SaleReturn`/`PurchaseReturn`/`StockAdjustment` (plan §7.2 PK names don't match EF convention).
- **EF migration:** `InitialCreate` generated and applied → valid SQLite schema (`apexpharma.db`, gitignored). Migration files committed.
- **Committed** local `main` `33ae93d` (fix + migration). Still nothing pushed to GitHub.

### 2026-07-01 — Session 1 (cont.) — Phase 1(a): Auth/RBAC + GitHub
- **Phase 1(a) implemented** (workflow: implement → security review): DI foundation (composition root in `App.xaml.cs`, `AddApexPharmaData` extension in the Data layer); **`AuthService`** — PBKDF2-SHA256, 100k iterations, 16-byte salt, self-describing hash, `FixedTimeEquals` verify, generic login failure (anti-enumeration); **RBAC** `Permission` enum + `HasPermission` matrix (plan §4); `DbInitializer` seeds 3 roles + one Owner (`admin` / `Admin@123`, change-on-first-login); `LoginWindow`/`LoginViewModel` gate the shell.
- **Security review = approve-with-nits;** all 5 findings fixed: unique `User.Username` index + duplicate guard (migration `AddUserUsernameUniqueIndex`), UTC timestamps, test-connection cleanup, role-resolution fail-safe (explicit name switch), DbContext DI moved into the Data layer.
- **Verified:** build 0/0; **56/56 tests pass**. Commit `f0c80a2` on `feature/auth-foundation` (off `main` `33ae93d`).
- **GitHub:** user authenticated `gh` (device flow) as **Shuvrajit10101** (`repo` scope). Repo confirmed **empty + private**.
- **Deviation to confirm:** Pharmacist currently has `ManageProducts` (catalog add/edit) but NOT `EditPrices` (Owner-only). Plan §4 only bars Pharmacist from prices/users/settings — defensible, but flag for client (one-flag reversal in `AuthService.HasPermission` + a test).

### 2026-07-01 — Session 1 (cont.) — Phase 1(b): Master Data
- **Implemented** (workflow: implement → review): CRUD services + validation for **Category, Manufacturer, Supplier, Product** (`MasterResult<T>` result pattern, DTO inputs, async over UnitOfWork); RBAC-gated (new **`ManageSuppliers`** permission for Owner+Pharmacist; catalog edits require `ManageProducts`); soft-delete `IsActive` added to Category/Manufacturer/Supplier; a permission-gated Masters window wired into `MainWindow`. Validation: India GST slabs {0,5,12,18,28}, unique names (NOCASE) + filtered-unique barcode, required FKs, 15-char GSTIN format, StateCode 01–37, non-negative reorder.
- **Migration** `AddMasterDataConstraints`: IsActive columns + UNIQUE NOCASE indexes (Category/Manufacturer name) + filtered-unique `Product.Barcode`.
- **Review = approve-with-nits;** fixes applied: per-session DI scope for the Masters window (was leaking a root-scoped DbContext), StateCode validation, guarded deactivation (block when active products reference the master). **The `ToLowerInvariant` nit was a FALSE POSITIVE** — those `ToLower()` calls are inside EF→SQLite queries where `ToLowerInvariant` has NO translation (21 tests failed); `ToLower()` maps to SQLite `lower()` (server-side, culture-independent). Kept `ToLower()` intentionally — **do not "fix" this.**
- **Verified:** build 0/0; **122/122 tests**. Commit `1a7dfab` → merged to `main` via **PR #6** (squash `a8e6da2`); CI green.

### 2026-07-01 — Session 1 (cont.) — UX: single-window navigation shell
- Owner tested the running app and flagged the separate-window Masters had **no "back"**. Built `feature/nav-shell`: `INavigationService` (DI singleton) swaps module views IN PLACE in `MainWindow` (ContentControl + DataTemplates); persistent left nav with active-item highlight; **Masters is now an embedded `MastersView` UserControl**; **placeholder views** for unbuilt modules; a landing view after login. DbContext lifetime: fresh DI **scope per module visit**, previous disposed (reviewed ✓, no leak).
- **Tests project retargeted** `net8.0` → `net8.0-windows` (+UseWPF) + Desktop project ref so nav logic is unit-testable; `InternalsVisibleTo("ApexPharma.Tests")` for a test-only resolver seam.
- **Review = approve-with-nits;** both fixed: navigation **re-entrancy guard** (monotonic token → last-click-wins) and **non-fatal activation** (failures show a status banner instead of crashing; global `DispatcherUnhandledException` handler logs to `%LocalAppData%\ApexPharma\error.log`).
- **Verified:** build 0/0; **136/136 tests**. Commits `b91b61c` + `4176938` → merged to `main` via **PR #7** (`ac99fef`); client §17 answers committed (`47095fa`). CI green.

### 2026-07-01 — Session 1 (cont.) — Phase 1(c): Purchase/GRN + Inventory
- **Purchase/GRN** (`PurchaseService`, ONE ACID transaction): record a supplier purchase; each line (product, batch no, expiry, qty, cost, MRP, GST) **upserts a `Batch`** (existing (product,batch) += qty, else new lot; `SalePrice` defaults to MRP) → stock-in; header GST via `GstService`. Expiry on/before today rejected; qty/price/GST-slab validated; **stock never negative**; whole purchase rolls back on any bad line. **Purchase returns** (over-return blocked). Gated on `DoPurchases`.
- **InventoryService** (read-only + FEFO + AdjustStock): stock grid with near-expiry(90d)/expired/low-stock flags; `SelectBatchFefoAsync` + `AdjustStockAsync` (transactional, non-negative) ready for billing.
- **UI:** Purchases + Inventory are now REAL nav-shell modules (per-visit scope); Inventory colour-codes expired/near-expiry/low-stock. Added `ISessionContext` (acting user → CreatedBy). `ViewStock` gates Inventory.
- **Migration** `AddBatchProductBatchNoIndex` (composite `Batch(ProductId,BatchNo)`).
- **Review = approve-with-nits;** fixed: **intra-purchase duplicate-batch merge** (same (product,batch) across lines → one lot via in-transaction dictionary), **AdjustStock tests** (commit+audit; below-zero rollback), single-read inventory.
- **Verified:** build 0/0; **171/171 tests**. Commits `5c08e7a` + `e7d5dae` on `feature/purchase-grn` (off `main` `47095fa`). Awaiting merge.

---

## Change & Decision Log
*(minor changes/recommendations applied — major changes need client sign-off)*

- **2026-07-01** — Renamed/consolidated the interim plan to the canonical `plan.md`. *(minor)*
- **2026-07-01** — Working product name **"PharmaDesk"** proposed in the plan; final name TBD by client (see Open Questions #8). *(assumption)*
- **2026-07-01** — Tech stack locked per `plan.md` §8 (.NET 8/WPF/SQLite). Change requires sign-off. *(decision)*
- **2026-07-01** — Toolchain installed **user-local (no admin)** because winget needed UAC; `.NET` at `%LocalAppData%\Microsoft\dotnet`, gh at `%LocalAppData%\Programs\gh`, both on User PATH. *(minor)*
- **2026-07-01** — Split returns into two entities `SaleReturn` + `PurchaseReturn` (plan §7.2 listed them combined); cleaner for EF. *(minor)*
- **2026-07-01** — `StockAdjustment` carries both `BatchId` and `ProductId` (batch integrity + product-level reporting). *(minor)*
- **2026-07-01** — Decimal precision via `[Column(TypeName="decimal(18,2)")]`; GST-rate columns `decimal(5,2)`. *(minor)*
- **2026-07-01** — RECOMMENDATION (open): move repo out of OneDrive (sync can lock `bin/obj`). Kept in place pending your call. *(recommendation)*
- **2026-07-01** — Auth: PBKDF2-SHA256 / 100k / 16-byte salt, self-describing hash; UTC for persisted timestamps. *(decision)*
- **2026-07-01** — DECISION TO CONFIRM: Pharmacist has `ManageProducts` (not `EditPrices`). Flip one flag if the client wants Pharmacist off the catalog. *(open)*
- **2026-07-01** — Runtime DB at `%LocalAppData%\ApexPharma\apexpharma.db` (off-repo); repo `apexpharma.db` stays gitignored / design-time only. *(decision)*
- **2026-07-01** — Added `ManageSuppliers` permission (Owner+Pharmacist) instead of overloading `ManageProducts`. *(minor)*
- **2026-07-01** — Added `IsActive` soft-delete to Category/Manufacturer/Supplier (Product already had it). *(minor)*
- **2026-07-01** — `ToLower()` (NOT `ToLowerInvariant()`) is INTENTIONAL in EF LINQ — maps to SQLite `lower()`, server-side + culture-independent; `ToLowerInvariant` has no EF-SQLite translation. **Do not "fix".** *(note)*
- **2026-07-01** — CLIENT ANSWERS (§17) resolved: 1 counter→SQLite; thermal-receipt-first; CSV/Excel importer; GST defaults-now; backup local+cloud; retail-only (Form 20/21); name **Apex-Pharma**. *(client sign-off)*
- **2026-07-01** — SCOPE (owner-approved): **credit-customer ledger (khata) is IN v1** (was optional/Phase-2). Build during the billing phase; `Customer.CreditLimit/Balance` already exist. *(major — owner signed off)*
- **2026-07-01** — Building a **single-window navigation shell** (`feature/nav-shell`) after the owner found the separate-window Masters had no "back"; placeholders for unbuilt modules. *(minor — UX fix)*

---

## Client Answers — RESOLVED 2026-07-01 (plan §17)
*(the owner answered all 8 in-session; folded into plan.md; GitHub issue #2 to be updated/closed)*

1. **Concurrent billing PCs → ONE.** Stay on **SQLite** (offline, single file). ✓ confirms current design.
2. **Credit customers (khata) → YES, in v1.** Add a customer ledger (balance, part-payments, outstanding). `Customer` already has `credit_limit`/`balance`. **Confirmed in v1** (build in the billing phase).
3. **Printer → UNDECIDED.** Default: build the **3-inch thermal** GST receipt first; keep A4/A5 easy to add.
4. **Existing data → UNSURE.** Build the **CSV/Excel importer** anyway; confirm the source later.
5. **GST/HSN → prefill defaults now** (HSN 3003/3004, 12%/5%), CA reconciles exact values before go-live.
6. **Backup → LOCAL + CLOUD folder.** Automatic daily **encrypted** backup to a local folder AND a Drive/OneDrive-synced folder + one-click restore.
7. **License/sales → RETAIL ONLY.** Retail DL (Form 20/21) on the bill; no wholesale/B2B. (Supersedes the 20B/21B question.)
8. **Name → "Apex-Pharma"** (no rename; matches repo + code + header).
9. ✅ **Repo state:** confirmed **empty + private**; `gh` authenticated as Shuvrajit10101.

---

## Next Steps (ordered)

1. ✅ Phase 0 · 1(a) auth · 1(b) masters · nav-shell · §17 answers · **1(c) Purchase/GRN (171 tests)** — merging.
2. **GitHub Expert:** merge `feature/purchase-grn` → `main`.
3. **.NET 8 → .NET 10 (LTS) upgrade** (owner-approved, all corners): install .NET 10 SDK; retarget every csproj/`global.json`/package; update CI (+ bump `checkout`/`setup-dotnet` actions); **reset migrations to one `InitialCreate` under EF 10** (no `8.x` stamps); update README/CLAUDE/plan/memory; full suite green on .NET 10; PR → merge.
4. **Phase 1(d) — POS billing:** GST + Schedule-H capture + FEFO batch pick + thermal receipt + **khata (credit)**; consumes InventoryService FEFO/AdjustStock.
5. Then (e) customer ledger/outstanding, (f) reports (low-stock/expiry/sales/Schedule-H/GST-HSN), (g) local+cloud backup.
6. Still open (non-blocking): Pharmacist permission (#3), branch-protection, OneDrive relocation.

---

## Archive
*(older session detail moved here to keep the top lean — none yet)*
