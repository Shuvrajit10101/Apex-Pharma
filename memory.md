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
- **Stack (locked):** .NET 10 (LTS) · C# · WPF (MVVM) · SQLite + EF Core · QuestPDF · xUnit · Git/GitHub · Inno Setup/MSIX.

## Current status

- **Phase:** **Phase 1 (Core MVP) — in progress (~90%).** Merged: 1(a)/1(b)/nav-shell/1(c)/1(d) billing/1(e) invoice+settings; **1(f) reports approved & merging**. Plus .NET 10 upgrade + NU1903 fix. **1(g) backup = the final slice.**
- **Done (all merged to `main`; 1(f) merging):** Phase 0; **1(a)** auth (PR #1); **1(b)** masters (PR #6); **nav-shell** (PR #7); §17 answers (issue #2 closed); **1(c)** Purchase/GRN + Inventory (PR #8); **.NET 10 (LTS)** upgrade (PR #9); **NU1903** fix (PR #11, issue #10 closed); **1(d)** POS billing — FEFO/GST/Schedule H-H1-X/bill-no/khata (PR #12); **1(e)** GST thermal invoice (QuestPDF) + Owner-only Settings + print/reprint (PR #13); **1(f)** reports — sales+profit, Schedule-H register, GST/HSN, low-stock/expiry (approve).
- **Repo:** live on GitHub, `main` @ `4136b02` (advances as 1(f)/1(g) merge); CI green (.NET 10 · checkout@v7 · setup-dotnet@v5). **241 tests, no vulnerable packages.** Branch protection unavailable (free private) → process-enforced.
- **Now:** **Completing Phase 1 in ONE GO, autonomously** — last slice: **1(g) backup/restore** (local + cloud). Owner reviews the whole of Phase 1 at the end. Rule #1 = **EXTREME agentic** (main session = pure orchestrator).
- **Next:** 1(g) backup → **Phase 1 COMPLETE → owner end-to-end review.**

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

### 2026-07-01 — Session 1 (cont.) — .NET 10 (LTS) upgrade
- **Installed .NET 10 SDK `10.0.301`** user-local at `%LocalAppData%\Microsoft\dotnet` (alongside the historical 8.0.422; `global.json` now pins `10.0.0` + `rollForward: latestFeature` so `dotnet --version` reports 10.0.301). First background install failed (`dotnet.exe` file-lock during zip extract — a stray dotnet build-server held it); killed processes and re-ran clean. Updated `dotnet-ef` global tool 8.0.28 → **10.0.9**.
- **Retargeted all 4 csproj** to `net10.0` (Domain/Data/Application) and `net10.0-windows` (Desktop + Tests); fixed `net8.0` mentions in csproj comments; bumped EF Core Sqlite/Design + DI/DI.Abstractions packages to `10.0.*`. Test-only packages (xunit, runner, Test.Sdk) left as-is (build green).
- **CI:** `dotnet-version` 8.0.x → **10.0.x**; `actions/checkout` v4 → **v7**, `actions/setup-dotnet` v4 → **v5** (both run on Node 24, clearing the Node-20 deprecation); fixed the `net10.0-windows` comment.
- **EF migrations reset:** deleted the 4 timestamped migrations + snapshot (kept `Migrations/README.md`), regenerated a single **`InitialCreate` under EF Core 10** (`ProductVersion 10.0.9`). Verified it reproduces the FULL schema — all indexes were already in `OnModelCreating`, so nothing was lost: `IX_Users_Username` (unique), NOCASE-unique `Category`/`Manufacturer` names, filtered-unique `Product.Barcode`, `IX_Products_Name`, unique `Sale.BillNo`, `Batch.ExpiryDate`, composite `Batch(ProductId,BatchNo)`. Applied to a fresh `apexpharma.db`; cleared the stale runtime dev DB so the app recreates it.
- **Docs:** README (runtime/SDK/download link/verify string + refreshed migration section), CLAUDE.md tech-stack line, plan.md §8 row, memory Stack line, and `Migrations/README.md` all → **.NET 10**.
- **Verified on .NET 10:** `dotnet build -c Release` → **0 errors** (only NU1903 advisory warnings on the transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 — pre-existing, not introduced by this upgrade); `dotnet test -c Release` → **171/171 pass**. Only remaining `.NET 8` strings are truthful historical session-log lines above. Committed on `feature/net10-upgrade`.

### 2026-07-01 — Session 1 (cont.) — Phase 1(d): POS billing
- **BillingService** (`CreateSaleAsync`, ONE ACID transaction): FEFO dispense across batches (earliest-expiry, non-expired, spans lots; expired blocked; insufficient stock → whole sale rejected); per-line CGST/SGST via `GstService`; **unique sequential `BillNo`** via a transactional `Billing.NextBillNo` Setting counter (self-heals from MAX+1; UNIQUE index backstop); **Schedule H/H1/X** require doctor + Rx (X gated same as H1; stricter narcotic register → Phase 2); **khata** — Credit payment requires a customer and adds the total to `Customer.Balance`; batch stock decremented transactionally (never negative); whole sale rolls back on any failure.
- **Bill-level discount** re-apportioned across lines (largest-remainder, to the paise) with **GST recomputed on the net** — Σ(SaleItem.LineTotal) == Sale.Total, header Discount == Σ line discounts (GST-correct for India: point-of-sale discount reduces taxable value).
- **CustomerService** (basic CRUD for credit customers) + **Billing UI** module (real, replacing the placeholder): product add → FEFO line preview → live totals → payment mode → customer picker (req. for Credit) → Schedule-H prompt → Complete Sale. `DoBilling` gated.
- **Review:** pass 1 = changes_requested (HIGH: Schedule X ungated; MEDIUM: bill-discount didn't reconcile) → fixed → re-review **approve-with-nits**. Deferred NIT: on-screen preview GST isn't recomputed on the bill-discounted net (display-only; authoritative receipt from BillingService is correct) — fold into the invoice/UI slice.
- **Verified:** build 0/0; **203/203 tests** on .NET 10. Commits `89f68f4` + `006e5cb` on `feature/pos-billing`. Awaiting merge.

### 2026-07-01 — Session 1 (cont.) — Phase 1(e): GST invoice + Settings
- **SettingsService + Settings module** (Owner-only, `ManageSettings`): pharmacy profile in the `Setting` key/value store — name, address, **GSTIN**, **DL number** (Form 20/21), phone, invoice footer, NearExpiryDays, tax-rounding; typed `PharmacyProfile`; defaults seeded.
- **InvoiceService** (QuestPDF **2026.6.1**, MIT; `LicenseType.Community` set at startup + a test ModuleInitializer): a **GST-compliant 80mm thermal receipt** — pharmacy header (GSTIN/DL/phone), bill no+date, cashier, customer, line items, **CGST/SGST tax-summary by rate/HSN**, subtotal/discount/round-off/total, payment mode, **Schedule-H doctor+Rx note**. Split into a layout-agnostic `InvoiceModel` (unit-testable) + `ThermalReceiptDocument` renderer (A4/A5 easy to add later). Print/preview via `IReceiptPrinter` (writes PDF to `%LocalAppData%\ApexPharma\Receipts`, opens default viewer); **reprint by bill no**.
- Fixed the deferred 1(d) **preview-GST nit** (live preview apportions the bill discount + recomputes GST on the net, matching the receipt).
- **Review = approve-with-nits**: the one LOW (tax-summary taxable footing) proved a **false alarm** — footing was already correct (`SaleItem.Discount` already includes the apportioned bill share); clarified the comment + added a regression test locking `Σ Taxable == Subtotal`. NIT (preview largest-remainder ≤0.01) left as acceptable display-only.
- **Verified:** build 0/0; **227/227 tests** on .NET 10; no vulnerable packages. Commits `9fb9a3e` + `6ae9155` on `feature/invoice-settings`. Awaiting merge.

### 2026-07-01 — Session 1 (cont.) — Phase 1(f): Reports
- **ReportService** (read-only): **Sales report** (per-bill + summary) with **profit** = Σ(line net ex-GST − Batch.PurchasePrice×qty, using the exact FEFO lot dispensed); **Schedule H/H1/X register** (legal — date, bill, drug, batch, qty, patient, **doctor**, **Rx**); **GST/HSN summary** (taxable/CGST/SGST grouped by HSN+rate, foots to the sales totals); **low-stock** + **near-expiry/expired** (reuse InventoryService).
- **Reports UI** (real module, replaces placeholder): report-type + date-range picker + grid + summary; **export** — CSV (RFC-4180 + BOM) for all, **A4 PDF** (QuestPDF) for the Schedule-H register + GST/HSN summary. `ViewReports` gated (Owner + Pharmacist). File I/O via `IReportFileService`.
- **Review = APPROVE** (clean). Tracked followups: **IST/UTC report day-boundary** (reports filter UTC `BillDate` vs local date → evening sales can shift day; convert the window to UTC before filtering — do before go-live) and the Cashier day-end/own-sales view.
- **Verified:** build 0/0; **241/241 tests** on .NET 10. Commit `f036616` on `feature/reports`. Awaiting merge.

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
- **2026-07-01** — **STACK: upgraded .NET 8 → .NET 10 (LTS)** (owner-directed — .NET 8 EOL Nov 2026; .NET 10 LTS to Nov 2028). All projects/packages/CI/docs; EF migrations reset to one `InitialCreate` under EF Core 10. `plan.md §8` + `CLAUDE.md` updated. *(major — owner signed off)*
- **2026-07-01** — SECURITY: pinned `SQLitePCLRaw.bundle_e_sqlite3` → **3.0.3** (Data+Tests) to clear NU1903 / GHSA-2m69-gcr7-jv3q (transitive 2.1.11); `dotnet list package --vulnerable` clean. *(fix — issue #10)*
- **2026-07-01** — Also updated the `/software` skill's lone .NET-version mention to ".NET 10 (current LTS)". *(minor)*
- **2026-07-01** — GOVERNANCE (owner-directed): CLAUDE.md **Rule #1 → EXTREME agentic** — main session is a pure orchestrator (dispatch / synthesize / report / govern only); ALL product code, builds, tests, git/gh, and reviews delegated to agents/workflows. *(process change)*
- **2026-07-01** — MODE (owner-directed): complete **all remaining Phase 1 in one go, autonomously** (billing → invoice+settings → reports → backup), each internally implement→review→fix→merge; owner reviews the whole Phase 1 together at the end, no per-slice pauses. *(process)*

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

1. ✅ Phase 0 · 1(a) auth · 1(b) masters · nav-shell · §17 answers · 1(c) Purchase/GRN · **.NET 10 upgrade** · **NU1903 fix** · **1(d) POS billing** · **1(e) invoice+settings** — all merged to `main` @ `4136b02` (227 tests, CI green).
2. ✅ **1(f) Reports** — sales+profit · low-stock · near-expiry/expired · **Schedule-H register** · GST/HSN — approve, 241 tests, merging.
3. **1(g) Backup & restore** — automatic daily local + cloud-folder encrypted backup + one-click restore — **running (final Phase-1 slice)**.
4. **→ Phase 1 COMPLETE → owner end-to-end review** (run the app; confirm: Schedule-X (a) vs (b), Pharmacist perms #3, branch protection, OneDrive relocation).
5. **Pre-go-live fix (found in 1f):** IST-aware report day boundaries — reports filter UTC `BillDate` vs local date, so evening sales can land in the adjacent day; convert the local window to UTC before filtering (affects daily sales/register/HSN).
6. **Phase 2** (post-review): returns UI · stock adjustments/expiry write-off · supplier & customer ledgers/statements · GST-return export · cloud sync · dashboards · stricter narcotic (Schedule-X) register/dual-Rx.
7. Still open (non-blocking): Pharmacist permission (#3), branch-protection, OneDrive relocation, Cashier day-end-only reports view.

---

## Archive
*(older session detail moved here to keep the top lean — none yet)*
