namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Produces a point-in-time, consistent copy of a live SQLite database and validates that a
/// candidate file is a real SQLite DB with the expected Apex-Pharma schema (plan.md §6.2, §13).
/// A raw file copy of an open WAL database is NOT safe (it can miss committed pages still in the
/// -wal file); the implementation uses SQLite's own consistent snapshot (<c>VACUUM INTO</c>).
/// Behind an interface so <see cref="BackupService"/> stays testable and the SQLite dependency is
/// isolated.
/// </summary>
public interface ISqliteSnapshotter
{
    /// <summary>
    /// Writes a consistent snapshot of the database at <paramref name="liveDbPath"/> to
    /// <paramref name="snapshotPath"/> (a fresh single-file SQLite DB, safe even while the app holds
    /// a connection).
    /// </summary>
    Task SnapshotAsync(string liveDbPath, string snapshotPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if <paramref name="dbPath"/> opens as a SQLite database that contains the core
    /// Apex-Pharma tables — used to reject a decrypted-but-not-really-a-DB (or wrong-schema) file
    /// before it is allowed to replace the live DB.
    /// </summary>
    Task<bool> IsValidApexDatabaseAsync(string dbPath, CancellationToken cancellationToken = default);
}
