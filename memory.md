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

- **Phase:** **Phase 1 (Core MVP) — in progress.** 1(a) merged; 1(b) Masters implemented + reviewed + fixed, merging next.
- **Done:** Phase 0; **Phase 1(a)** auth/RBAC (merged to `main` via PR #1, CI green); **Phase 1(b)** master data — Category/Manufacturer/Supplier/Product CRUD + validation + management UI, `AddMasterDataConstraints` migration, new `ManageSuppliers` permission. **122/122 tests**, build 0/0, reviewed (approve-with-nits, findings fixed). Commit `1a7dfab` on `feature/masters`.
- **Repo:** live on GitHub, `main` @ `dc87cda`; CI green. Branch protection unavailable (free private repo) → process-enforced.
- **Now:** GitHub Expert to PR/merge `feature/masters` → `main`.
- **Next (Phase 1c):** Purchase / GRN → batches (stock in).

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
- **Verified:** build 0/0; **122/122 tests**. Commit `1a7dfab` on `feature/masters` (off `main` `dc87cda`). Not yet merged.

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

---

## Open Questions — awaiting client answers
*(from `plan.md` §17 — resolve these before/early in Phase 1)*

1. **Concurrent billing PCs?** 1 → SQLite; 2+ concurrent → LAN PostgreSQL/SQL Server Express.
2. **Credit customers (khata)** needed in v1, or later?
3. **Hardware:** barcode scanner model, printer type (thermal 3" vs A5 laser), counter PC Windows version/specs.
4. **Existing data** to import (product list + current stock with batch/expiry)? Format?
5. **GST/HSN source:** who provides per-product HSN codes and GST rates?
6. **Cloud backup** provider (OneDrive/Google Drive/S3) or local-only for now?
7. **Invoice branding & DL type** (retail 20B / wholesale 21B), logo, header/footer.
8. **Product name:** confirm "PharmaDesk" or the client's preferred name.
9. ✅ **Repo state:** confirmed **empty + private**; `gh` authenticated as Shuvrajit10101. (Resolved 2026-07-01.)

---

## Next Steps (ordered)

1. ✅ Phase 0 · ✅ Phase 1(a) auth (merged, PR #1) · ✅ Phase 1(b) Masters (122/122 tests, fixed) — **merging now**.
2. **GitHub Expert:** PR/merge `feature/masters` → `main`; close issue #4; confirm CI green.
3. **Phase 1(c) — Purchase / GRN:** record supplier purchases with batch + expiry → creates batches (stock in); supplier basics.
4. Then (d) POS billing (GST + Schedule-H + FEFO + invoice), (e) low-stock/expiry + sales reports, (f) backup.
5. Client (non-blocking): Open Questions (issue #2); branch-protection decision.

---

## Archive
*(older session detail moved here to keep the top lean — none yet)*
