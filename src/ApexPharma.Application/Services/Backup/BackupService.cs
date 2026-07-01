using System.Globalization;
using System.Security.Cryptography;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Encrypted SQLite backup &amp; restore (plan.md §6.1 backup, §13, §14). Produces a consistent,
/// AES-256-GCM-encrypted, timestamped snapshot of the live database to a local folder and, when
/// configured, a cloud-synced folder; runs an auto-daily backup; prunes to a retention count; and
/// restores a chosen backup by decrypt → schema-validate → atomic swap so a bad backup can never
/// corrupt live data.
/// <para>
/// <b>Encryption scheme:</b> the snapshot is taken with SQLite <c>VACUUM INTO</c> (consistent even
/// while the app holds a connection), then encrypted with AES-256-GCM (authenticated) via
/// <see cref="BackupCrypto"/>. The 32-byte key comes from an <see cref="IBackupKeyProvider"/>:
/// by default a random data-key wrapped with Windows DPAPI (CurrentUser) so the backup is
/// decryptable ONLY by this user on this machine; optionally PBKDF2 from an Owner passphrase.
/// The passphrase/key is never stored in plaintext. Restore decrypts to a temp file, validates it
/// opens as a SQLite DB with the Apex-Pharma schema, then atomically moves it over the target —
/// on any failure the live DB is left untouched.
/// </para>
/// All money/stock rules are elsewhere; this service is data protection + I/O (plan.md §8 layering).
/// Backup and restore are gated on <see cref="Permission.Backup"/> (Owner only, plan.md §4).
/// </summary>
public sealed class BackupService : IBackupService
{
    internal const string FileExtension = ".bak";
    internal const string FilePrefix = "apexpharma-";
    internal const string TimestampFormat = "yyyyMMdd-HHmmss";
    internal const int DefaultRetention = 30;
    internal const string PendingRestoreSuffix = ".pending-restore";

    private readonly BackupOptions _options;
    private readonly ISettingsService _settings;
    private readonly IBackupKeyProvider _keyProvider;
    private readonly ISqliteSnapshotter _snapshotter;
    private readonly IAuthService _auth;

    public BackupService(
        BackupOptions options,
        ISettingsService settings,
        IBackupKeyProvider keyProvider,
        ISqliteSnapshotter snapshotter,
        IAuthService auth)
    {
        _options = options;
        _settings = settings;
        _keyProvider = keyProvider;
        _snapshotter = snapshotter;
        _auth = auth;
    }

    // ---- IBackupService (kept for the existing stub contract) -------------------------------

    /// <inheritdoc />
    public Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
        // Backdoor-free default: the parameterless contract runs as Owner (only the Owner can
        // reach the backup UI; the RBAC-aware overload is what callers use).
        => CreateBackupAsync(UserRole.Owner, cancellationToken);

    /// <inheritdoc />
    public Task RestoreBackupAsync(string backupFilePath, CancellationToken cancellationToken = default)
        => RestoreFromBackupAsync(backupFilePath, UserRole.Owner, cancellationToken)
            .ContinueWith(
                t => { if (!t.Result.Succeeded) throw new InvalidOperationException(t.Result.Error); },
                cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // ---- Backup ------------------------------------------------------------------------------

    /// <summary>
    /// Creates an encrypted backup as the acting role. Snapshots the live DB, encrypts it, writes
    /// the timestamped file to the local folder and (if set) the cloud folder, records the success
    /// time, and prunes old local backups. Returns the local backup path on success.
    /// </summary>
    public async Task<string> CreateBackupAsync(UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.Backup))
        {
            throw new UnauthorizedAccessException("You do not have permission to run backups.");
        }

        string localFolder = await ResolveLocalFolderAsync(cancellationToken);
        Directory.CreateDirectory(localFolder);

        string fileName = FilePrefix + DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture) + FileExtension;
        string localPath = Path.Combine(localFolder, fileName);

        // 1) Consistent snapshot to a private temp file.
        string tempSnapshot = Path.Combine(Path.GetTempPath(), "apex-snap-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await _snapshotter.SnapshotAsync(_options.LiveDatabasePath, tempSnapshot, cancellationToken);

            // 2) Encrypt (authenticated AES-256-GCM). Read plaintext, wipe it after encrypting.
            byte[] plaintext = await File.ReadAllBytesAsync(tempSnapshot, cancellationToken);
            byte[] key = await _keyProvider.GetKeyAsync(cancellationToken);
            byte[] cipher;
            try
            {
                cipher = BackupCrypto.Encrypt(plaintext, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(key);
            }

            // 3) Write local atomically (temp-then-move) so a reader never sees a half-written file.
            await WriteFileAtomicAsync(localPath, cipher, cancellationToken);

            // 4) Copy to the cloud-synced folder if configured (non-fatal: local already succeeded).
            string cloudFolder = await _settings.GetStringAsync(BackupKeys.CloudFolder, string.Empty, cancellationToken);
            if (!string.IsNullOrWhiteSpace(cloudFolder))
            {
                try
                {
                    Directory.CreateDirectory(cloudFolder);
                    await WriteFileAtomicAsync(Path.Combine(cloudFolder, fileName), cipher, cancellationToken);
                }
                catch
                {
                    // A missing/unavailable cloud folder must not fail the (already-durable) local backup.
                }
            }

            // 5) Record success + prune retention.
            await _settings.SetStringAsync(BackupKeys.LastBackupUtc, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
            await PruneAsync(localFolder, cancellationToken);

            return localPath;
        }
        finally
        {
            TryDelete(tempSnapshot);
        }
    }

    /// <summary>
    /// Runs the auto-daily backup if enabled and no successful backup exists for today (local date).
    /// Non-throwing: intended to be fired-and-forgotten at startup / on a timer so a backup failure
    /// never blocks or crashes the counter app. Returns the path if a backup ran, else null.
    /// </summary>
    public async Task<string?> RunDailyIfDueAsync(UserRole actingRole, CancellationToken cancellationToken = default)
    {
        try
        {
            bool enabled = await GetBoolAsync(BackupKeys.AutoBackupEnabled, defaultValue: true, cancellationToken);
            if (!enabled || !_auth.HasPermission(actingRole, Permission.Backup))
            {
                return null;
            }

            string lastRaw = await _settings.GetStringAsync(BackupKeys.LastBackupUtc, string.Empty, cancellationToken);
            if (DateTime.TryParse(lastRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime lastUtc)
                && lastUtc.ToLocalTime().Date == DateTime.Now.Date)
            {
                return null; // already backed up today
            }

            return await CreateBackupAsync(actingRole, cancellationToken);
        }
        catch
        {
            // Best-effort background task — swallow so a transient failure doesn't surface a crash.
            return null;
        }
    }

    // ---- Restore -----------------------------------------------------------------------------

    /// <summary>
    /// Validates and stages a restore of the LIVE database from <paramref name="backupPath"/>. The
    /// app holds the live DB connection, so the swap can't be done in-process safely; instead the
    /// decrypted+validated snapshot is written to a pending-restore file beside the live DB and
    /// applied atomically on the next startup (<see cref="ApplyPendingRestoreIfAnyAsync"/>). Returns
    /// a result asking the caller to restart; the live DB is untouched until then.
    /// </summary>
    public async Task<MasterResult> RestoreFromBackupAsync(string backupPath, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.Backup))
        {
            return MasterResult.Fail("You do not have permission to restore backups.");
        }

        // Decrypt + validate into a temp file first. Any failure returns here WITHOUT touching live data.
        string tempDb = Path.Combine(Path.GetTempPath(), "apex-restore-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            MasterResult decrypted = await DecryptAndValidateAsync(backupPath, tempDb, cancellationToken);
            if (!decrypted.Succeeded)
            {
                return decrypted;
            }

            // Stage beside the live DB; the actual swap happens at startup before EF opens the DB.
            string pending = _options.LiveDatabasePath + PendingRestoreSuffix;
            TryDelete(pending);
            File.Move(tempDb, pending);
            tempDb = string.Empty; // moved — don't delete in finally

            return MasterResult.Ok();
        }
        catch (Exception ex)
        {
            return MasterResult.Fail($"Restore failed: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempDb))
            {
                TryDelete(tempDb);
            }
        }
    }

    /// <summary>
    /// Restores a backup directly into <paramref name="targetDbPath"/> (decrypt → validate → atomic
    /// move). Used by tests and any caller that owns the target (i.e. the target is not open). A
    /// corrupt/garbage backup or wrong key is rejected before the target is touched — the atomic
    /// move only happens after validation succeeds, so the target is never left half-written.
    /// </summary>
    public async Task<MasterResult> RestoreToAsync(string backupPath, string targetDbPath, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.Backup))
        {
            return MasterResult.Fail("You do not have permission to restore backups.");
        }

        string tempDb = Path.Combine(Path.GetTempPath(), "apex-restore-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            MasterResult decrypted = await DecryptAndValidateAsync(backupPath, tempDb, cancellationToken);
            if (!decrypted.Succeeded)
            {
                return decrypted;
            }

            string? dir = Path.GetDirectoryName(targetDbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Atomic replace: File.Move with overwrite is atomic on the same volume, so the target
            // is either the old DB or the fully-validated new one — never a partial file.
            File.Move(tempDb, targetDbPath, overwrite: true);
            tempDb = string.Empty;
            return MasterResult.Ok();
        }
        catch (Exception ex)
        {
            return MasterResult.Fail($"Restore failed: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempDb))
            {
                TryDelete(tempDb);
            }
        }
    }

    /// <summary>
    /// Applies a pending restore staged by <see cref="RestoreFromBackupAsync"/>, called ONCE at
    /// startup before EF opens the DB. Atomically swaps the validated snapshot in and clears any
    /// stale WAL/SHM side files. Idempotent and safe: with no pending file it does nothing. Returns
    /// true if a restore was applied.
    /// </summary>
    public bool ApplyPendingRestoreIfAnyAsync()
    {
        string pending = _options.LiveDatabasePath + PendingRestoreSuffix;
        if (!File.Exists(pending))
        {
            return false;
        }

        // The snapshot was already schema-validated at stage time. Drop the WAL/SHM side files so
        // SQLite can't replay stale journal pages onto the new DB, then overwrite the live DB with
        // the validated snapshot. Copy-then-delete (not Move) so a transient lock on the destination
        // — a sync client / AV scanner briefly holding the old file — is handled by retry, and the
        // pending file (our source of truth) survives a failed attempt to be retried next startup.
        TryDelete(_options.LiveDatabasePath + "-wal");
        TryDelete(_options.LiveDatabasePath + "-shm");

        ReplaceWithRetry(pending, _options.LiveDatabasePath);
        TryDelete(pending);
        return true;
    }

    // ---- Listing / retention -----------------------------------------------------------------

    /// <summary>Lists the recent backups in the local folder, newest first (for the UI).</summary>
    public async Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        string localFolder = await ResolveLocalFolderAsync(cancellationToken);
        return EnumerateBackups(localFolder)
            .OrderByDescending(b => b.TimestampLocal)
            .ToList();
    }

    private async Task PruneAsync(string localFolder, CancellationToken cancellationToken)
    {
        int retention = await _settings.GetIntAsync(BackupKeys.RetentionCount, DefaultRetention, cancellationToken);
        if (retention <= 0)
        {
            retention = DefaultRetention;
        }

        List<BackupInfo> all = EnumerateBackups(localFolder)
            .OrderByDescending(b => b.TimestampLocal)
            .ToList();

        foreach (BackupInfo old in all.Skip(retention))
        {
            TryDelete(old.Path);
        }
    }

    private static IEnumerable<BackupInfo> EnumerateBackups(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(folder, FilePrefix + "*" + FileExtension))
        {
            var fi = new FileInfo(path);
            DateTime ts = ParseTimestamp(fi.Name) ?? fi.LastWriteTime;
            yield return new BackupInfo(path, fi.Name, ts, fi.Length);
        }
    }

    private static DateTime? ParseTimestamp(string fileName)
    {
        // apexpharma-YYYYMMDD-HHmmss.bak
        string core = fileName;
        if (core.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            core = core[FilePrefix.Length..];
        }
        if (core.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            core = core[..^FileExtension.Length];
        }

        return DateTime.TryParseExact(core, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime ts)
            ? ts
            : null;
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private async Task<MasterResult> DecryptAndValidateAsync(string backupPath, string tempDbOut, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            return MasterResult.Fail("Backup file not found.");
        }

        byte[] cipher = await File.ReadAllBytesAsync(backupPath, cancellationToken);
        byte[] key = await _keyProvider.GetKeyAsync(cancellationToken);
        byte[] plaintext;
        try
        {
            plaintext = BackupCrypto.Decrypt(cipher, key);
        }
        catch (CryptographicException)
        {
            // Wrong key / tampered / not-a-backup — GCM auth failure. Reject; nothing was touched.
            return MasterResult.Fail("The backup could not be decrypted. It may be corrupt, tampered with, or from a different machine/user.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        await File.WriteAllBytesAsync(tempDbOut, plaintext, cancellationToken);
        CryptographicOperations.ZeroMemory(plaintext);

        if (!await _snapshotter.IsValidApexDatabaseAsync(tempDbOut, cancellationToken))
        {
            TryDelete(tempDbOut);
            return MasterResult.Fail("The backup did not contain a valid Apex-Pharma database.");
        }

        return MasterResult.Ok();
    }

    private async Task<string> ResolveLocalFolderAsync(CancellationToken cancellationToken)
    {
        string configured = await _settings.GetStringAsync(BackupKeys.LocalFolder, string.Empty, cancellationToken);
        return string.IsNullOrWhiteSpace(configured) ? _options.DefaultLocalFolder : configured;
    }

    private async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken cancellationToken)
    {
        string raw = await _settings.GetStringAsync(key, defaultValue ? "true" : "false", cancellationToken);
        return bool.TryParse(raw, out bool value) ? value : defaultValue;
    }

    /// <summary>
    /// Copies <paramref name="source"/> over <paramref name="destination"/>, retrying briefly to
    /// ride out a transient lock (sync client / AV scanner) on the destination. Throws if it still
    /// can't after the retries, leaving the caller's pending file intact to retry next startup.
    /// </summary>
    private static void ReplaceWithRetry(string source, string destination)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static async Task WriteFileAtomicAsync(string finalPath, byte[] bytes, CancellationToken cancellationToken)
    {
        string temp = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        File.Move(temp, finalPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp file is harmless.
        }
    }
}
