namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Supplies the 32-byte AES-256 key used to encrypt/decrypt backups (plan.md §14). The key is
/// obtained soundly and is bound to this machine/user (DPAPI) or re-derived from the Owner's
/// passphrase (PBKDF2); it is never stored in plaintext. Abstracting key acquisition keeps the
/// crypto scheme swappable and lets tests inject a fixed key without DPAPI.
/// </summary>
public interface IBackupKeyProvider
{
    /// <summary>
    /// Returns the AES-256 key for backup crypto. May initialise/persist a wrapped key on first
    /// use (DPAPI scheme). Throws if the key cannot be obtained (e.g. wrong/missing passphrase),
    /// so callers surface a clear error rather than writing an unrecoverable backup.
    /// </summary>
    Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default);
}
