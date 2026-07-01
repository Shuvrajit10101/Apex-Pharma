using ApexPharma.Application.Services.Backup;

namespace ApexPharma.Tests;

/// <summary>
/// A deterministic in-memory <see cref="IDpapiProtector"/> for tests — prepends a marker and XORs
/// with a fixed pad so protect/unprotect round-trips and an "unowned" blob (missing the marker)
/// throws, mirroring how the real DPAPI rejects blobs sealed by another user/machine. Not real
/// DPAPI (which is a Windows OS call) — just enough to exercise the wrap/unwrap key flow.
/// </summary>
internal sealed class FakeDpapiProtector : IDpapiProtector
{
    private static readonly byte[] Marker = { 0xDE, 0xAD, 0xBE, 0xEF };
    private const byte Pad = 0x5A;

    public byte[] Protect(byte[] plaintext)
    {
        byte[] body = plaintext.Select(b => (byte)(b ^ Pad)).ToArray();
        return Marker.Concat(body).ToArray();
    }

    public byte[] Unprotect(byte[] protectedBlob)
    {
        if (protectedBlob.Length < Marker.Length || !protectedBlob.Take(Marker.Length).SequenceEqual(Marker))
        {
            throw new System.Security.Cryptography.CryptographicException("Blob was not protected by this (fake) user/machine.");
        }

        return protectedBlob.Skip(Marker.Length).Select(b => (byte)(b ^ Pad)).ToArray();
    }
}

/// <summary>A fixed-key <see cref="IBackupKeyProvider"/> so crypto is deterministic in service tests.</summary>
internal sealed class FixedKeyProvider : IBackupKeyProvider
{
    private readonly byte[] _key;

    public FixedKeyProvider(byte fill = 0x2A) => _key = Enumerable.Repeat(fill, BackupCrypto.KeySize).ToArray();

    public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult((byte[])_key.Clone());
}
