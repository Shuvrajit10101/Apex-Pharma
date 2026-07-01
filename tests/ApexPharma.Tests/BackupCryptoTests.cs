using System.Security.Cryptography;
using System.Text;
using ApexPharma.Application.Services.Backup;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// AES-256-GCM backup crypto (plan.md §14): encryption is real (no plaintext SQLite header leaks),
/// round-trips exactly, and a wrong key or any tampering fails the GCM authentication tag rather
/// than yielding garbage — the property the restore path relies on to reject corrupt backups.
/// </summary>
public class BackupCryptoTests
{
    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, BackupCrypto.KeySize).ToArray();

    [Fact]
    public void Encrypt_Then_Decrypt_RoundTripsExactly()
    {
        byte[] key = Key(0x11);
        byte[] plaintext = Encoding.UTF8.GetBytes("SQLite format 3\0 ... some database bytes ...");

        byte[] cipher = BackupCrypto.Encrypt(plaintext, key);
        byte[] roundTripped = BackupCrypto.Decrypt(cipher, key);

        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public void Ciphertext_DoesNotContain_PlaintextSqliteHeader()
    {
        byte[] key = Key(0x22);
        // A real SQLite file starts with the ASCII magic "SQLite format 3\0".
        byte[] sqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
        byte[] plaintext = sqliteHeader.Concat(Enumerable.Repeat((byte)0x7, 512)).ToArray();

        byte[] cipher = BackupCrypto.Encrypt(plaintext, key);

        // The encrypted blob must NOT contain the plaintext SQLite header anywhere.
        Assert.False(ContainsSubsequence(cipher, sqliteHeader),
            "Encrypted backup must not contain the plaintext 'SQLite format 3' header.");
    }

    [Fact]
    public void Decrypt_WithWrongKey_Throws()
    {
        byte[] cipher = BackupCrypto.Encrypt(Encoding.UTF8.GetBytes("secret data"), Key(0x33));

        Assert.Throws<AuthenticationTagMismatchException>(() => BackupCrypto.Decrypt(cipher, Key(0x44)));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        byte[] key = Key(0x55);
        byte[] cipher = BackupCrypto.Encrypt(Encoding.UTF8.GetBytes("secret data that is long enough"), key);

        // Flip a byte in the ciphertext region (past magic+nonce+tag).
        cipher[^1] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => BackupCrypto.Decrypt(cipher, key));
    }

    [Fact]
    public void Decrypt_GarbageBytes_Throws_NotRecognisedFormat()
    {
        byte[] garbage = RandomNumberGenerator.GetBytes(200);
        Assert.Throws<CryptographicException>(() => BackupCrypto.Decrypt(garbage, Key(0x66)));
    }

    [Fact]
    public void Encrypt_WrongKeySize_Throws()
    {
        Assert.Throws<ArgumentException>(() => BackupCrypto.Encrypt(new byte[10], new byte[16]));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
