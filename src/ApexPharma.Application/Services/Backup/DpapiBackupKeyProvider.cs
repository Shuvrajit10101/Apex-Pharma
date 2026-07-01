using System.Security.Cryptography;
using ApexPharma.Application.Services.Settings;

namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Default backup key scheme (plan.md §14): a random 32-byte AES-256 <b>data key</b> is generated
/// once, wrapped with Windows DPAPI (<see cref="IDpapiProtector"/>, CurrentUser), and the wrapped
/// blob persisted (base64) in the <see cref="BackupKeys.WrappedKey"/> setting. The plaintext key
/// never touches disk or the DB — only the DPAPI-sealed blob does, and that blob is decryptable
/// ONLY by this Windows user on this machine. On every backup/restore the provider unwraps the blob
/// to recover the key in memory. This needs no passphrase for the Owner to remember, which suits a
/// single-PC counter deployment.
/// </summary>
public sealed class DpapiBackupKeyProvider : IBackupKeyProvider
{
    private readonly ISettingsService _settings;
    private readonly IDpapiProtector _dpapi;

    public DpapiBackupKeyProvider(ISettingsService settings, IDpapiProtector dpapi)
    {
        _settings = settings;
        _dpapi = dpapi;
    }

    /// <inheritdoc />
    public async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
    {
        string wrappedBase64 = await _settings.GetStringAsync(BackupKeys.WrappedKey, string.Empty, cancellationToken);

        if (!string.IsNullOrEmpty(wrappedBase64))
        {
            byte[] wrapped = Convert.FromBase64String(wrappedBase64);
            // Unseal with DPAPI. Throws CryptographicException if this isn't the owning user/machine
            // — a clear, correct failure rather than a silently wrong key.
            return _dpapi.Unprotect(wrapped);
        }

        // First use on this machine: mint a fresh random key, seal it, and persist the sealed blob.
        byte[] key = RandomNumberGenerator.GetBytes(BackupCrypto.KeySize);
        byte[] sealedBlob = _dpapi.Protect(key);
        await _settings.SetStringAsync(BackupKeys.WrappedKey, Convert.ToBase64String(sealedBlob), cancellationToken);
        await _settings.SetStringAsync(BackupKeys.KeyScheme, "Dpapi", cancellationToken);
        return key;
    }
}
