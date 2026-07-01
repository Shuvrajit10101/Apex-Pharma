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

- **Phase:** **Phase 0 (Setup) — in progress.**
- **Done:** planning + governance; **.NET 8 solution scaffolded** (67 files, layered per plan §8) and committed to local `main` (`eb76f36`).
- **In flight:** installing the toolchain (.NET 8 SDK user-local + gh CLI). Build/test not yet verified (waiting on the SDK).
- **Not started:** EF initial migration, CI run, GitHub push (needs your auth), Phase 1 features.

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
9. **Repo state:** is https://github.com/Shuvrajit10101/Apex-Pharma empty and ready for the GitHub Expert to scaffold?

---

## Next Steps (ordered)

1. **Verify the scaffold builds:** once the SDK finishes, `dotnet restore` → `dotnet build -c Release` → `dotnet test` (GST tests must pass). Fix any hand-authored compile issues.
2. **Generate the initial EF migration:** `dotnet ef migrations add InitialCreate -p src/ApexPharma.Data -s src/ApexPharma.Desktop`; confirm the SQLite DB is created.
3. **GitHub (needs your OK):** `gh auth login` → GitHub Expert adds the remote, pushes `main`, sets branch protection + a board mirroring the roadmap.
4. Get answers to the **Open Questions** (BA turns them into tracked issues).
5. Begin **Phase 1 — Core MVP** per `plan.md` §15 (masters → purchase/GRN with batch+expiry → GST POS billing → core reports → backup).

---

## Archive
*(older session detail moved here to keep the top lean — none yet)*
