using System.Security.Cryptography;

namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Authenticated encryption for backup files (plan.md §14) using <b>AES-256-GCM</b>.
/// <para>
/// File layout (all lengths fixed, big-endian not needed as they are constants):
/// <code>
///   magic[8] = "APXBAK01"   — format marker + version
///   nonce[12]               — random 96-bit GCM nonce (unique per file)
///   tag[16]                 — 128-bit GCM authentication tag
///   ciphertext[...]         — AES-256-GCM ciphertext of the SQLite snapshot
/// </code>
/// GCM is an <i>authenticated</i> cipher: decryption verifies the tag, so a wrong key or any
/// tampering (a flipped byte, a truncated file) fails cleanly with
/// <see cref="CryptographicException"/> instead of yielding garbage — this is what lets a
/// corrupt/garbage backup be rejected before it can touch the live DB. The plaintext SQLite
/// header (<c>"SQLite format 3\0"</c>) never appears in the encrypted bytes.
/// </para>
/// The key is a 32-byte AES-256 key supplied by an <see cref="IBackupKeyProvider"/> (DPAPI-wrapped
/// random key, or PBKDF2 from the Owner's passphrase). This class only does the symmetric crypto;
/// it never persists or derives keys itself.
/// </summary>
public static class BackupCrypto
{
    /// <summary>8-byte format marker + version. Bumped if the container layout ever changes.</summary>
    internal static readonly byte[] Magic = "APXBAK01"u8.ToArray();

    internal const int KeySize = 32;   // AES-256
    internal const int NonceSize = 12; // 96-bit GCM nonce (recommended)
    internal const int TagSize = 16;   // 128-bit GCM tag

    private static int HeaderSize => Magic.Length + NonceSize + TagSize;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under <paramref name="key"/> into the versioned,
    /// authenticated container described on the class. A fresh random nonce is generated per call.
    /// </summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ValidateKey(key);

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[plaintext.Length];

        // The caller owns the input `plaintext` buffer, so we don't zero it here; but we take care
        // not to leave any *internal* copy of it lingering in the managed heap. (Encrypt keeps no
        // internal plaintext copy — `ciphertext`/`output` hold only encrypted bytes — so there is
        // nothing extra to wipe; the symmetric wipe lives in Decrypt, which does materialise plaintext.)
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        byte[] output = new byte[HeaderSize + ciphertext.Length];
        int offset = 0;
        Buffer.BlockCopy(Magic, 0, output, offset, Magic.Length); offset += Magic.Length;
        Buffer.BlockCopy(nonce, 0, output, offset, NonceSize); offset += NonceSize;
        Buffer.BlockCopy(tag, 0, output, offset, TagSize); offset += TagSize;
        Buffer.BlockCopy(ciphertext, 0, output, offset, ciphertext.Length);
        return output;
    }

    /// <summary>
    /// Decrypts a container produced by <see cref="Encrypt"/>. Throws
    /// <see cref="CryptographicException"/> if the magic is wrong, the file is truncated, the key
    /// is wrong, or the ciphertext/tag was tampered with (GCM authentication failure).
    /// </summary>
    public static byte[] Decrypt(byte[] container, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(container);
        ValidateKey(key);

        if (container.Length < HeaderSize)
        {
            throw new CryptographicException("Backup file is too small or truncated.");
        }

        // Verify the format marker before doing any crypto so a non-backup file fails clearly.
        for (int i = 0; i < Magic.Length; i++)
        {
            if (container[i] != Magic[i])
            {
                throw new CryptographicException("Not a recognised Apex-Pharma backup file.");
            }
        }

        int offset = Magic.Length;
        byte[] nonce = new byte[NonceSize];
        Buffer.BlockCopy(container, offset, nonce, 0, NonceSize); offset += NonceSize;
        byte[] tag = new byte[TagSize];
        Buffer.BlockCopy(container, offset, tag, 0, TagSize); offset += TagSize;

        int cipherLen = container.Length - HeaderSize;
        byte[] ciphertext = new byte[cipherLen];
        Buffer.BlockCopy(container, offset, ciphertext, 0, cipherLen);

        byte[] plaintext = new byte[cipherLen];
        bool success = false;
        try
        {
            using var aes = new AesGcm(key, TagSize);

            // AesGcm.Decrypt throws AuthenticationTagMismatchException (a CryptographicException) on
            // a wrong key or any tampering — exactly the integrity guarantee we rely on.
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            success = true;
            return plaintext;
        }
        finally
        {
            // On failure, zero the transient plaintext buffer so no partial/authenticated DB
            // plaintext or key material lingers in the managed heap. On success the caller owns the
            // returned buffer and is responsible for wiping it (BackupService does, in a finally).
            if (!success)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Backup key must be {KeySize} bytes (AES-256).", nameof(key));
        }
    }
}
