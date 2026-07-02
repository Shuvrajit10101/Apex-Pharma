using System.Collections.Concurrent;
using ApexPharma.Application.Services.Backup;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Domain.Enums;

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

/// <summary>
/// A thread-safe, in-memory <see cref="ISettingsService"/> for concurrency tests. The production
/// <c>SettingsService</c> is backed by a single (non-thread-safe) EF <c>DbContext</c>; this double
/// lets many callers hit get/set in true parallel so the DPAPI provider's single-flight key minting
/// (Fix 1) can be exercised without EF's shared-context threading getting in the way.
/// </summary>
internal sealed class ConcurrentSettingsStore : ISettingsService
{
    private readonly ConcurrentDictionary<string, string?> _values = new();

    public Task SeedDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> GetStringAsync(string key, string fallback = "", CancellationToken cancellationToken = default)
        => Task.FromResult(_values.TryGetValue(key, out string? v) && v is not null ? v : fallback);

    public Task<int> GetIntAsync(string key, int fallback, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.TryGetValue(key, out string? v) && int.TryParse(v, out int parsed) ? parsed : fallback);

    public Task SetStringAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<PharmacyProfile> GetProfileAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<MasterResult> SaveProfileAsync(PharmacyProfile profile, UserRole actingRole, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// A <see cref="FakeDpapiProtector"/> that counts <see cref="Protect"/> calls (thread-safe), so a
/// concurrency test can assert exactly ONE key was ever minted+wrapped under single-flight minting.
/// </summary>
internal sealed class CountingDpapiProtector : IDpapiProtector
{
    private readonly FakeDpapiProtector _inner = new();
    private int _protectCount;

    public int ProtectCount => Volatile.Read(ref _protectCount);

    public byte[] Protect(byte[] plaintext)
    {
        Interlocked.Increment(ref _protectCount);
        return _inner.Protect(plaintext);
    }

    public byte[] Unprotect(byte[] protectedBlob) => _inner.Unprotect(protectedBlob);
}
