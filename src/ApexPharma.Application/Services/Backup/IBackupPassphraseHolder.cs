namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Holds the backup passphrase for the CURRENT session in memory only (never persisted), so the
/// passphrase key scheme can derive its key on backup/restore (plan.md §14). The UI writes the
/// passphrase here (write-only in the UI) when the Owner enters it; the composite key provider
/// reads it. Cleared when the session ends. This is the seam that keeps the plaintext passphrase
/// out of the DB and off disk.
/// </summary>
public interface IBackupPassphraseHolder
{
    /// <summary>The passphrase for this session, or null if none has been supplied.</summary>
    string? Passphrase { get; }

    /// <summary>Sets (or clears with null) the session passphrase.</summary>
    void Set(string? passphrase);
}

/// <summary>Default in-memory <see cref="IBackupPassphraseHolder"/> — a simple session holder.</summary>
public sealed class BackupPassphraseHolder : IBackupPassphraseHolder
{
    public string? Passphrase { get; private set; }

    public void Set(string? passphrase) => Passphrase = passphrase;
}
