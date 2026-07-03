# Go-Live Restore Drill — Apex-Pharma

**Audience:** the pharmacy Owner.
**Do this once before you start real trading on any counter PC** — and again before every go-live (e.g. after a Windows reinstall, a new PC, or a move). A backup you have never restored is not a backup you can trust. This runbook proves, on the actual machine, that a backup restores cleanly and the data comes back.

Everything below matches how the app's backup engine (`BackupService`) actually behaves — folder, filename, encryption, and the restart-to-apply restore flow.

---

## 1. Why this matters (and when to run it)

A backup only protects you if a restore actually works on *this* PC, as *this* Windows user. Hardware dies, disks corrupt, ransomware happens. The drill is a go-live checklist item: **back up → copy the file off this machine → restore it → verify the data is all there.** Run it:

- Once **before the pharmacy goes live** on a counter PC.
- Again whenever the machine, Windows user, or backup key scheme changes.
- Periodically (e.g. monthly) as a health check.

---

## 2. Read this first — the key-scheme caveat

Backups are **AES-256-GCM encrypted**. The 32-byte key comes from one of two schemes, and this decides **who and where** a backup can be restored:

- **DPAPI (default).** The data-key is wrapped with **Windows DPAPI (CurrentUser)**. A DPAPI backup decrypts **ONLY as the same Windows user on the same PC**. Copy it to another PC, or sign in as a different Windows user, and it **cannot** be decrypted — by design.
  - **Implication for off-site recovery:** a DPAPI backup is a same-machine/same-user safety net (accidental deletion, bad edit, a botched update). It is **not** a disaster-recovery copy — if the PC or the Windows profile is lost, a DPAPI backup on a USB stick is unreadable.
- **Passphrase (optional).** The key is derived (PBKDF2) from an Owner passphrase. A passphrase backup can be restored **on any PC / any user that has the passphrase**. This is the scheme to use if you want true off-site recovery. **If you lose the passphrase, the backup is unrecoverable** — it is never stored in plaintext anywhere.

> Decide which scheme you are on **before** the drill. If off-site recovery matters, use a passphrase and keep it somewhere safe and separate from the backups.

---

## 3. The drill, step by step

### (a) Trigger and locate a backup

1. In the app: **Settings → Backup & restore → Back up now.**
   (A backup also runs **automatically once a day** if auto-backup is on and today has no successful backup yet.)
2. The encrypted file is written to the **local backup folder**:
   - Default: **`%LocalAppData%\ApexPharma\Backups`** (i.e. `C:\Users\<you>\AppData\Local\ApexPharma\Backups`), unless the Owner has picked a different folder in Settings.
   - Filename pattern: **`apexpharma-YYYYMMDD-HHmmss.bak`** (local timestamp), e.g. `apexpharma-20260703-181500.bak`.
3. **Optional cloud copy.** If a cloud-synced folder is configured, the same file is also copied there. The local backup is the durable one; a **cloud-copy failure is reported** on screen (e.g. "Local backup OK; cloud copy failed: …") rather than hidden — so if you rely on the off-site copy, **read the status message** and don't assume the cloud copy exists just because the local one succeeded.
4. Old local backups are pruned to the retention count (default **30**), newest kept.

Confirm the `.bak` file exists in the folder with today's timestamp before continuing.

### (b) Restore it

> The live database is **untouched** until you restart the app. A restore is *staged*, then applied atomically on the **next startup**.

1. In the app: **Settings → Backup & restore → Restore → pick a `.bak` file.**
2. The app **decrypts and validates** the file into a temporary location first:
   - It must decrypt with this machine/user's key (DPAPI) or the passphrase.
   - It must open as a valid Apex-Pharma SQLite database.
3. On success, the validated snapshot is staged **beside the live DB** as **`apexpharma.db.pending-restore`**, and you are asked to restart. **The live DB is not changed yet.**
4. **Close and reopen the app.** On the next startup, before it opens the database, the app:
   - clears any stale WAL/SHM side files (`apexpharma.db-wal`, `apexpharma.db-shm`),
   - atomically swaps the staged snapshot in as the live DB,
   - deletes the `*.pending-restore` file.

   That's the whole restore: **Restore → close & reopen → applied.**

**A bad backup changes nothing.** A garbage/foreign/wrong-key/wrong-user file is **rejected at step 2** with a clear message, before anything is staged — so a failed restore leaves your live data exactly as it was.

### (c) Verify

After the app reopens:

1. Confirm expected data is present — a few known **products**, the **last bill / invoice number**, and the latest **day-end** figures all look right for the moment the backup was taken.
2. Confirm the restore fully applied: there should be **no leftover `apexpharma.db.pending-restore`** file next to the database. (If one remains, the swap didn't complete — restart once more; the pending file is retained precisely so a transient lock can be retried on the next startup.)
3. Record the drill: **date · which `.bak` · restored OK / issues.** Keep this log with your go-live checklist.

---

## 4. Drill checklist (copy/tick each go-live)

- [ ] **Back up** now (Settings → Backup & restore → Back up now).
- [ ] Confirm `apexpharma-YYYYMMDD-HHmmss.bak` exists in `%LocalAppData%\ApexPharma\Backups` (or your chosen folder).
- [ ] **Copy the `.bak` off this machine** (USB / cloud). *(Reminder: a DPAPI backup only restores on this PC+user — see §2.)*
- [ ] **Restore** it (Settings → Backup & restore → Restore → pick the file).
- [ ] **Close and reopen** the app so the restore applies.
- [ ] **Verify**: known products, last bill number, and day-end are present; no leftover `*.pending-restore`.
- [ ] **Record**: date, file name, result.
- [ ] Repeat before **every** go-live.

---

## 5. Recovery notes & troubleshooting

- **"The backup could not be decrypted…"** — the file is corrupt, tampered with, or from a **different machine/user** (DPAPI), or the **passphrase is wrong**. Nothing was touched. Use a backup made by this same user on this PC, or supply the correct passphrase.
- **"…did not contain a valid Apex-Pharma database."** — the file decrypted but isn't an Apex-Pharma DB. Pick a genuine `apexpharma-*.bak`.
- **WAL / SHM files.** SQLite may leave `apexpharma.db-wal` and `apexpharma.db-shm` beside the live DB in normal use. The restore **deletes these** before swapping so no stale journal pages replay onto the restored data — you don't need to touch them.
- **Leftover `*.pending-restore` after restart.** The swap hit a transient lock (a sync client or antivirus briefly holding the file). The pending file is kept so it retries automatically — **restart the app once more**. If it persists, close any tool that might be holding the database folder open, then restart.
- **Never hand-edit the `.bak` or copy a live `apexpharma.db` into the backups folder** — backups are encrypted and validated; a raw copy will be rejected on restore.
