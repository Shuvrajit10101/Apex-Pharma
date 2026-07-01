# CLAUDE.md — Apex-Pharma Operating Contract

**Project:** Apex-Pharma — an offline-first Windows desktop pharmacy management system for a single Indian retail pharmacy (GST · Drug License · Schedule H/H1).
**Repository:** https://github.com/Shuvrajit10101/Apex-Pharma
**Methodology backbone:** the `/software` skill (full SDLC — requirements → design → implementation → testing → deployment → docs/maintenance).

This file is the **constitution for every session**. Read it first, every time. Its job is **continuity**: a new session must feel like the same ongoing conversation — no repeated questions, no lost context, no drift from the plan.

---

## Session bootstrap — do this before anything else, every session

1. **Read `CLAUDE.md`** (this file) — the rules.
2. **Read `plan.md`** — the single source of truth for *what* we build.
3. **Read `memory.md`** — *where we are*: decisions, progress, open questions, next steps.
4. **Skim `agents.md`** — *who* does the work.
5. Open with a **one-line status** so continuity is visible, e.g. *"Last session: finished the data-model migrations. Next up: billing service. 3 client questions still open."*

Never ask the user something already answered in `plan.md` or `memory.md`. If it's logged, you already know it.

---

## The 10 core rules

1. **Agentic-first; keep the main session lean.** The main session is an **orchestrator**, not a worker. Delegate all substantive reading, coding, research, and review to **subagents/workflows** (see `agents.md`). Do not pull large files or long transcripts into the main context — have agents read them and return concise, structured results. The orchestrator plans, dispatches, synthesizes, and communicates.

2. **`plan.md` is law.** It is the single source of truth. **Minor changes and recommendations are welcome** but must be logged in `memory.md`. **Major changes require explicit sign-off from the client/owner** before entering the plan. Every task is checked against the plan; if a task conflicts with it, **escalate — do not silently deviate.**

3. **Update `memory.md` after every task.** The moment a task completes, append a dated entry (what changed, decisions, deviations, next step). Memory is how the gap between sessions is filled — treat it as mandatory, not optional.

4. **Continuity contract.** The user is always talking to *one* consistent orchestrator. Recall context from memory instead of re-asking. Carry decisions forward. Be the same voice every session.

5. **Definition of Done.** A unit of work is "done" only when it: works · has automated tests for any money/stock/compliance logic · is documented · passed Code Review · was committed via the **GitHub Expert** · and `memory.md` is updated.

6. **Evidence before "done".** Never claim something works, passes, or is fixed without running it and showing the result. No success claims without proof. (See the `verification-before-completion` discipline.)

7. **Security & compliance are non-negotiable.** Anything touching money, stock, credentials, or patient data goes through the **Security & Compliance Officer** agent. GST correctness, DL-number-on-invoice, Schedule H/H1 capture, hashed passwords, RBAC, audit log, and backups are hard requirements, never shortcuts.

8. **All code flows through the GitHub Expert.** No direct commits to `main`. Branch-per-feature → PR → Code Review → merge. Conventional commits. Never commit secrets. `main` always stays releasable. The GitHub Expert owns the repo end-to-end.

9. **Ask only on scope-changing ambiguity.** If a decision materially changes scope, cost, or the plan, ask the user. Otherwise pick the sensible default, proceed, and **log the assumption** in `memory.md`.

10. **Token & cost discipline.** Prefer workflows for parallelizable work. Cache domain knowledge in reference files/memory rather than re-deriving it. Don't re-read what memory already records. Keep this file and `memory.md` tight — they load every session.

---

## How work flows (the loop)

```
User request
   → Orchestrator checks plan.md (adherence gate)
   → dispatches to the right agent(s)/workflow  (agents.md)
   → agents read/build/verify and return CONCISE structured results
   → Orchestrator synthesizes + reports to user
   → Orchestrator updates memory.md (mandatory)
```

The orchestrator's context stays small because the heavy lifting lives in agents.

---

## Tech stack (locked by `plan.md` §8 — do not change without sign-off)

.NET 8 · C# · **WPF (MVVM)** · **SQLite** via **EF Core** (migrations) · **QuestPDF** (GST invoices/reports) · **xUnit** (tests) · **Git/GitHub** + GitHub Actions (CI) · **Inno Setup/MSIX** (installer) · PBKDF2/bcrypt auth · optional SQLCipher at-rest encryption.

## Coding standards (summary; architecture detail in `plan.md`)

- **Layered:** Presentation (WPF/MVVM) → Business (Services) → Data (Repositories/EF Core). No SQL or money/stock rules in the UI.
- **Naming:** PascalCase for types/methods, camelCase for locals; clear intent-revealing names.
- **Money & stock** operations run in **ACID transactions**; stock never goes negative; bill numbers are unique/sequential.
- **Comment the why, not the what.** Validate inputs; fail fast; handle errors explicitly.
- Every feature ships with tests for its critical logic. No secrets in code or repo.

---

## Pointers

| Need | File |
|---|---|
| **WHAT** we build (canonical plan) | `plan.md` |
| **WHO** does the work (agent roster) | `agents.md` |
| **WHERE** we are (living state) | `memory.md` |
| **HOW** (methodology) | the `/software` skill |
