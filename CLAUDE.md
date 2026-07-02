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

1. **Agentic-first to the EXTREME — the main session is a pure orchestrator, always clean.** The main session does the *absolute minimum* itself and delegates *everything substantive* to subagents/workflows (see `agents.md`). Maximize the share of work done by agents; keep the orchestrator's context small at all times.
   - **The orchestrator does ONLY:** (a) session bootstrap reads (this file, `plan.md`, `memory.md`, `agents.md`); (b) plan & decide; (c) dispatch subagents/workflows; (d) synthesize their concise structured results; (e) communicate with the user; (f) maintain its own governance files (`memory.md` / `plan.md` / `CLAUDE.md` / `agents.md`); (g) at most a single trivial status check (e.g. one `git status`).
   - **Everything else is delegated — no exceptions:** all product code writing/editing, builds, tests, `dotnet` / `git` / `gh` operations, migrations, research, code review, and any read of a large file or multi-file change go to agents. Prefer **workflows** (parallel / multi-stage agents) over single agents whenever the work parallelizes.
   - **Never pull weight into the main context:** don't read large files or long transcripts — agents read them and return short structured summaries. If you catch yourself about to build, edit product code, run a long command, or grep a big file directly: **stop and dispatch an agent instead.**
   - The orchestrator plans, dispatches, synthesizes, reports, and governs — and stays small.

2. **`plan.md` is law.** It is the single source of truth. **Minor changes and recommendations are welcome** but must be logged in `memory.md`. **Major changes require explicit sign-off from the client/owner** before entering the plan. Every task is checked against the plan; if a task conflicts with it, **escalate — do not silently deviate.**

3. **Update `memory.md` after EVERY step — immediately, never deferred.** The instant any unit of work finishes (a slice merged, a decision made, an install, a relocation, a repo/visibility/config change, a phase transition, a notable build/test result), update `memory.md` in the SAME turn, *before* starting the next action. This is TWO mandatory parts: **(a)** append a dated **Session-Log** entry (what changed · decisions · evidence · next step); **and (b)** edit the living **Current Status** and **Next Steps** in place so they always match reality right now (correct `main` SHA, current phase, what's running, what's next). **Never say "I'll update memory later" or batch it to a future merge — deferral is a rule violation.** Before ending any turn, glance at Current Status: if it doesn't describe reality *this moment*, fix it first. Memory is the only thing that survives between sessions — a stale memory silently breaks continuity. *(Commit timing is separate: the file must be current immediately; the commit to `main` rides with the next governance commit, since the single working tree can't be branch-switched while an agent is mid-slice.)*

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

.NET 10 · C# · **WPF (MVVM)** · **SQLite** via **EF Core** (migrations) · **QuestPDF** (GST invoices/reports) · **xUnit** (tests) · **Git/GitHub** + GitHub Actions (CI) · **Inno Setup/MSIX** (installer) · PBKDF2/bcrypt auth · optional SQLCipher at-rest encryption.

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
