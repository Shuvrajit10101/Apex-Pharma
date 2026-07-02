namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// The <see cref="Domain.Entities.Setting"/> keys that configure backup &amp; restore
/// (plan.md §6.1 backup, §13, §14). Grouped under the <c>Backup.*</c> namespace so the
/// key/value store stays self-describing. The passphrase itself is NEVER stored here in
/// plaintext — <see cref="WrappedKey"/> holds a DPAPI/PBKDF2-derived, machine/user-bound
/// blob from which the AES key is unwrapped, and it is meaningless off this machine/user
/// (plan.md §14 data protection).
/// </summary>
public static class BackupKeys
{
    /// <summary>Local backup folder (absolute path). Blank until the Owner sets it.</summary>
    public const string LocalFolder = "Backup.LocalFolder";

    /// <summary>Optional cloud-synced folder (OneDrive/Google-Drive). Blank = local only.</summary>
    public const string CloudFolder = "Backup.CloudFolder";

    /// <summary>How many recent backups to keep locally before pruning the oldest (default 30).</summary>
    public const string RetentionCount = "Backup.RetentionCount";

    /// <summary>"true"/"false" — run the auto-daily backup on startup / timer.</summary>
    public const string AutoBackupEnabled = "Backup.AutoBackupEnabled";

    /// <summary>ISO-8601 UTC timestamp of the last <b>successful</b> backup (drives auto-daily).</summary>
    public const string LastBackupUtc = "Backup.LastBackupUtc";

    /// <summary>
    /// The wrapped data-key blob (base64). For the DPAPI scheme this is a random 32-byte
    /// AES key sealed with Windows DPAPI (CurrentUser); for the passphrase scheme this key
    /// is not persisted at all (it is re-derived from the passphrase). Never a plaintext key.
    /// </summary>
    public const string WrappedKey = "Backup.WrappedKey";

    /// <summary>
    /// Which key scheme is in force: <c>Dpapi</c> (default — machine/user-bound, no passphrase
    /// to remember) or <c>Passphrase</c> (PBKDF2 from an Owner-set passphrase, portable but the
    /// passphrase must be remembered). Stored so restore knows how to obtain the key.
    /// </summary>
    public const string KeyScheme = "Backup.KeyScheme";

    /// <summary>Base64 PBKDF2 salt for the passphrase scheme (public — safe to store).</summary>
    public const string PassphraseSalt = "Backup.PassphraseSalt";

    /// <summary>Base64 verifier so a wrong passphrase is caught early with a clear message.</summary>
    public const string PassphraseVerifier = "Backup.PassphraseVerifier";
}
