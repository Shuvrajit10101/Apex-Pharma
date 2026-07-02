using System.IO;
using System.Text;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Backup;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// BackupService round-trip, encryption, restore, retention, and RBAC (plan.md §6.1 backup, §12,
/// §13, §14). Uses a real on-disk SQLite DB (VACUUM INTO + atomic file moves need a real file), a
/// fixed-key crypto provider for determinism, and an in-memory settings store.
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly SqliteFileDatabase _db = new(productCount: 7);
    private readonly SqliteInMemoryContext _settingsCtx = new();
    private readonly SettingsService _settings;
    private readonly BackupService _sut;
    private readonly BackupOptions _options;
    private readonly string _backupFolder;

    public BackupServiceTests()
    {
        var auth = new AuthService(_settingsCtx.Context);
        _settings = new SettingsService(_settingsCtx.Context, auth);
        _settings.SeedDefaultsAsync().GetAwaiter().GetResult();

        _backupFolder = Path.Combine(_db.Directory, "backups");
        _options = new BackupOptions(_db.DbPath, _backupFolder);

        _sut = new BackupService(
            _options,
            _settings,
            new FixedKeyProvider(),
            new SqliteSnapshotter(),
            auth);
    }

    public void Dispose()
    {
        _db.Dispose();
        _settingsCtx.Dispose();
    }

    [Fact]
    public async Task CreateBackup_ThenRestore_ReproducesData()
    {
        int originalProducts = _db.ProductCount();

        string backupPath = await _sut.CreateBackupAsync(UserRole.Owner);
        Assert.True(File.Exists(backupPath));

        // Restore into a fresh target DB and confirm the data matches exactly.
        string targetDb = Path.Combine(_db.Directory, "restored.db");
        MasterResult result = await _sut.RestoreToAsync(backupPath, targetDb, UserRole.Owner);
        Assert.True(result.Succeeded, result.Error);

        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ApexPharma.Data.ApexPharmaDbContext>()
            .UseSqlite($"Data Source={targetDb}")
            .Options;
        await using var restored = new ApexPharma.Data.ApexPharmaDbContext(options);
        Assert.Equal(originalProducts, restored.Products.Count());
        Assert.Equal("known-value", restored.Settings.First(s => s.Key == "Test.Marker").Value);
    }

    [Fact]
    public async Task Backup_File_IsEncrypted_NotPlaintextSqlite()
    {
        string backupPath = await _sut.CreateBackupAsync(UserRole.Owner);
        byte[] bytes = await File.ReadAllBytesAsync(backupPath);

        // A raw SQLite file starts with "SQLite format 3\0"; the encrypted backup must not.
        byte[] header = Encoding.ASCII.GetBytes("SQLite format 3\0");
        Assert.False(StartsWith(bytes, header));
        Assert.False(Contains(bytes, header));
    }

    [Fact]
    public async Task Restore_WithWrongKey_Fails_AndDoesNotTouchTarget()
    {
        string backupPath = await _sut.CreateBackupAsync(UserRole.Owner);

        // A different service instance with a DIFFERENT key can't decrypt.
        var wrongKeyService = new BackupService(
            _options, _settings, new FixedKeyProvider(0xFF), new SqliteSnapshotter(), new AuthService(_settingsCtx.Context));

        // Pre-populate a target file so we can prove it's untouched on failure.
        string targetDb = Path.Combine(_db.Directory, "target.db");
        await File.WriteAllTextAsync(targetDb, "SENTINEL");

        MasterResult result = await wrongKeyService.RestoreToAsync(backupPath, targetDb, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Equal("SENTINEL", await File.ReadAllTextAsync(targetDb));
    }

    [Fact]
    public async Task Restore_GarbageBackup_IsRejected_TargetUntouched()
    {
        string garbage = Path.Combine(_db.Directory, "garbage.bak");
        await File.WriteAllBytesAsync(garbage, System.Security.Cryptography.RandomNumberGenerator.GetBytes(500));

        string targetDb = Path.Combine(_db.Directory, "target2.db");
        await File.WriteAllTextAsync(targetDb, "SENTINEL");

        MasterResult result = await _sut.RestoreToAsync(garbage, targetDb, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Equal("SENTINEL", await File.ReadAllTextAsync(targetDb));
    }

    [Fact]
    public async Task Restore_ValidCipherButNotADatabase_IsRejected()
    {
        // Encrypt some NON-database plaintext with the same key so decryption succeeds but the
        // schema validation must still reject it — proving validation, not just decryption, gates.
        byte[] key = await new FixedKeyProvider().GetKeyAsync();
        byte[] cipher = BackupCrypto.Encrypt(Encoding.UTF8.GetBytes("this is not a sqlite database"), key);
        string fakeBackup = Path.Combine(_db.Directory, "notadb.bak");
        await File.WriteAllBytesAsync(fakeBackup, cipher);

        string targetDb = Path.Combine(_db.Directory, "target3.db");
        await File.WriteAllTextAsync(targetDb, "SENTINEL");

        MasterResult result = await _sut.RestoreToAsync(fakeBackup, targetDb, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("valid", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SENTINEL", await File.ReadAllTextAsync(targetDb));
    }

    [Fact]
    public async Task Retention_PrunesToConfiguredCount()
    {
        await _settings.SetStringAsync(BackupKeys.RetentionCount, "3");

        // Create several backups (unique filenames via timestamp — force distinct names).
        for (int i = 0; i < 6; i++)
        {
            await CreateBackupWithName($"apexpharma-2026010{i}-120000.bak");
        }

        // Trigger a real backup which runs the prune.
        await _sut.CreateBackupAsync(UserRole.Owner);

        IReadOnlyList<BackupInfo> remaining = await _sut.ListBackupsAsync();
        Assert.Equal(3, remaining.Count);
    }

    [Fact]
    public async Task ApplyPendingRestore_SwapsInStagedDatabase()
    {
        // Stage a restore of the LIVE DB from a backup, then apply it as startup would.
        string backupPath = await _sut.CreateBackupAsync(UserRole.Owner);

        // Corrupt the live DB by replacing it, so we can see the swap actually happen.
        await File.WriteAllTextAsync(_db.DbPath, "not a db anymore");

        MasterResult staged = await _sut.RestoreFromBackupAsync(backupPath, UserRole.Owner);
        Assert.True(staged.Succeeded, staged.Error);
        Assert.True(File.Exists(_db.DbPath + BackupService.PendingRestoreSuffix));

        bool applied = _sut.ApplyPendingRestoreIfAnyAsync();
        Assert.True(applied);
        Assert.False(File.Exists(_db.DbPath + BackupService.PendingRestoreSuffix));

        // The live DB is now the restored SQLite DB again.
        Assert.True(await new SqliteSnapshotter().IsValidApexDatabaseAsync(_db.DbPath));
    }

    [Fact]
    public async Task CreateBackup_AsNonOwner_IsRefused()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateBackupAsync(UserRole.Cashier));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateBackupAsync(UserRole.Pharmacist));

        // And no backup file was written.
        IReadOnlyList<BackupInfo> backups = await _sut.ListBackupsAsync();
        Assert.Empty(backups);
    }

    [Fact]
    public async Task RestoreFromBackup_AsNonOwner_IsRefused()
    {
        // Make a real backup as Owner first.
        string backupPath = await _sut.CreateBackupAsync(UserRole.Owner);

        MasterResult cashier = await _sut.RestoreFromBackupAsync(backupPath, UserRole.Cashier);
        Assert.False(cashier.Succeeded);
        Assert.Contains("permission", cashier.Error!, StringComparison.OrdinalIgnoreCase);

        MasterResult pharmacist = await _sut.RestoreToAsync(backupPath, Path.Combine(_db.Directory, "x.db"), UserRole.Pharmacist);
        Assert.False(pharmacist.Succeeded);
        Assert.Contains("permission", pharmacist.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloudCopy_IsWritten_WhenConfigured()
    {
        string cloud = Path.Combine(_db.Directory, "cloud");
        await _settings.SetStringAsync(BackupKeys.CloudFolder, cloud);

        string localPath = await _sut.CreateBackupAsync(UserRole.Owner);

        string cloudCopy = Path.Combine(cloud, Path.GetFileName(localPath));
        Assert.True(File.Exists(cloudCopy));
        Assert.Equal(await File.ReadAllBytesAsync(localPath), await File.ReadAllBytesAsync(cloudCopy));
    }

    [Fact]
    public async Task CloudCopy_Failure_IsFlagged_ButLocalBackupSucceeds()
    {
        // Fix 2 (data-safety): a cloud-copy failure must be REPORTED, not swallowed, so the Owner is
        // never misled into believing an off-site copy exists. Make the cloud path unusable by
        // placing a FILE where the cloud FOLDER is expected — Directory.CreateDirectory then throws.
        string cloudPath = Path.Combine(_db.Directory, "cloud-as-file");
        await File.WriteAllTextAsync(cloudPath, "not a directory");
        await _settings.SetStringAsync(BackupKeys.CloudFolder, cloudPath);

        BackupResult result = await _sut.CreateBackupWithResultAsync(UserRole.Owner);

        // Local backup is present and durable...
        Assert.True(File.Exists(result.LocalPath));
        // ...but the cloud copy is flagged as failed with a clear warning.
        Assert.False(result.CloudCopySucceeded);
        Assert.NotNull(result.CloudWarning);
        Assert.Contains("cloud", result.CloudWarning!, StringComparison.OrdinalIgnoreCase);

        // No cloud copy was written next to the offending file.
        Assert.False(File.Exists(Path.Combine(cloudPath, Path.GetFileName(result.LocalPath))));
    }

    [Fact]
    public async Task Snapshot_OfWalModeDatabase_Succeeds_AndRoundTrips()
    {
        // Fix 3 (data-safety): a WAL-mode DB keeps committed pages in the -wal side file. Put the
        // live DB into WAL mode with an uncheckpointed committed change, then back up + restore and
        // confirm the snapshot captured a consistent point-in-time view (including the WAL page).
        const string marker = "wal-committed-value";
        await using (var wal = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db.DbPath}"))
        {
            await wal.OpenAsync();
            await using (var pragma = wal.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                await pragma.ExecuteNonQueryAsync();
            }
            await using (var insert = wal.CreateCommand())
            {
                // Committed, but deliberately NOT checkpointed — the row lives in the -wal file.
                insert.CommandText = "INSERT INTO Settings (Key, Value) VALUES ('Wal.Marker', $v);";
                insert.Parameters.AddWithValue("$v", marker);
                await insert.ExecuteNonQueryAsync();
            }
            // Leave WAL/-shm in place (no checkpoint) to exercise the read-write snapshot path.
        }

        string backupPath = await _sut.CreateBackupAsync(UserRole.Owner);
        Assert.True(File.Exists(backupPath));

        string targetDb = Path.Combine(_db.Directory, "wal-restored.db");
        MasterResult result = await _sut.RestoreToAsync(backupPath, targetDb, UserRole.Owner);
        Assert.True(result.Succeeded, result.Error);

        var options = new DbContextOptionsBuilder<ApexPharma.Data.ApexPharmaDbContext>()
            .UseSqlite($"Data Source={targetDb};Pooling=False")
            .Options;
        await using var restored = new ApexPharma.Data.ApexPharmaDbContext(options);
        Assert.Equal(marker, restored.Settings.First(s => s.Key == "Wal.Marker").Value);
    }

    // Writes a valid encrypted backup file under a chosen name (to control retention ordering).
    private async Task CreateBackupWithName(string fileName)
    {
        Directory.CreateDirectory(_backupFolder);
        var snap = new SqliteSnapshotter();
        string temp = Path.Combine(_db.Directory, Guid.NewGuid().ToString("N") + ".snap");
        await snap.SnapshotAsync(_db.DbPath, temp);
        byte[] key = await new FixedKeyProvider().GetKeyAsync();
        byte[] cipher = BackupCrypto.Encrypt(await File.ReadAllBytesAsync(temp), key);
        await File.WriteAllBytesAsync(Path.Combine(_backupFolder, fileName), cipher);
        File.Delete(temp);
    }

    private static bool StartsWith(byte[] data, byte[] prefix) =>
        data.Length >= prefix.Length && data.Take(prefix.Length).SequenceEqual(prefix);

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }
}
