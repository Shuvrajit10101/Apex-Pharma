using Microsoft.Data.Sqlite;

namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// <see cref="ISqliteSnapshotter"/> over <c>Microsoft.Data.Sqlite</c>. Snapshots via
/// <c>VACUUM INTO</c>, which asks SQLite itself to write a clean, defragmented, transactionally
/// consistent copy of the whole database (including any pages sitting in the WAL) to a new file —
/// safe even while the app holds its own EF connection. Validation opens the candidate read-only
/// and checks the core tables exist, so a decrypted-but-corrupt or wrong-schema file is rejected
/// before it can replace the live DB (plan.md §6.2, §13).
/// </summary>
public sealed class SqliteSnapshotter : ISqliteSnapshotter
{
    // A handful of core tables whose presence proves this is a real Apex-Pharma database.
    private static readonly string[] RequiredTables = { "Users", "Roles", "Products", "Sales", "Settings" };

    /// <inheritdoc />
    public async Task SnapshotAsync(string liveDbPath, string snapshotPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(liveDbPath);
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);

        // Never leave a stale snapshot target; VACUUM INTO refuses to overwrite an existing file.
        if (File.Exists(snapshotPath))
        {
            File.Delete(snapshotPath);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = liveDbPath,
            // Open read-WRITE (never create). A WAL-mode database keeps committed pages in the
            // `-wal` side file, and SQLite must be able to touch `-wal`/`-shm` to hand VACUUM INTO a
            // transactionally consistent view — a ReadOnly connection can be refused access to those
            // side files (read-only media / restrictive permissions), failing the backup. VACUUM
            // INTO itself only reads the source and writes the *target*, so the live DB is not
            // mutated; this stays a point-in-time consistent snapshot.
            Mode = SqliteOpenMode.ReadWrite,
            // No pooling: a read-write connection holds a file lock, so the handle must be fully
            // released on dispose — a lingering pooled handle would block a later atomic
            // move/replace of the live DB (e.g. an immediately-following restore).
            Pooling = false,
        };

        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // Parameter binding isn't supported for VACUUM's target, so quote the path safely by
        // doubling single quotes (SQLite string-literal escaping).
        string escaped = snapshotPath.Replace("'", "''");
        command.CommandText = $"VACUUM main INTO '{escaped}';";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsValidApexDatabaseAsync(string dbPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
        {
            return false;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                // No pooling: the connection must fully release the file handle on dispose so a
                // subsequent atomic move/replace of the DB isn't blocked by a lingering pooled handle.
                Pooling = false,
            };

            await using var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            // Integrity check first — catches a truncated/corrupt page store that still has a valid header.
            await using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                object? result = await integrity.ExecuteScalarAsync(cancellationToken);
                if (result is not string ok || !string.Equals(ok, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            foreach (string table in RequiredTables)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
                cmd.Parameters.AddWithValue("$name", table);
                object? count = await cmd.ExecuteScalarAsync(cancellationToken);
                if (Convert.ToInt64(count) == 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (SqliteException)
        {
            // Not a SQLite file (e.g. random/garbage bytes that happened to decrypt) — reject.
            return false;
        }
    }
}
