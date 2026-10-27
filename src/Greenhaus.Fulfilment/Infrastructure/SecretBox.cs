using System.Security.Cryptography;
using System.Text;
using Greenhaus.Fulfilment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Fulfilment.Infrastructure;

/// <summary>
/// Seals the packing secrets (the claim token and the broker password of a device that is being packed) before they reach the
/// database. AES-GCM with a random nonce per value, and the allocation, the device and the field as associated data, so a sealed
/// value that is moved to another row or to another column does not open. The stored form is `enc1:` and base64 of nonce, tag and
/// ciphertext. A value that does not open is reported, never guessed at and never returned as it is.
/// </summary>
public sealed class SecretBox
{
    public const string Prefix = "enc1:";
    public const string ClaimTokenField = "claim_token";
    public const string CredentialField = "credential";
    private const int NonceSize = 12, TagSize = 16, KeySize = 32;

    private readonly byte[] _key;

    public SecretBox(byte[] key)
    {
        if (key.Length != KeySize) throw new ArgumentException($"The secrets key must be {KeySize} bytes", nameof(key));
        _key = key;
    }

    /// <summary>True when the key was made up for this run, so sealed secrets do not survive a restart.</summary>
    public bool Ephemeral { get; private init; }

    /// <summary>
    /// The box for the configured key (base64 of 32 bytes). Outside development a missing or bad key stops the service from starting,
    /// because packing secrets must never be stored without protection. In development a missing key is replaced by a random one.
    /// </summary>
    public static SecretBox Create(string? base64Key, bool development)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            if (!development) throw new InvalidOperationException("Fulfilment:SecretsKey is required: it protects the claim tokens and device passwords in the database. Set it to base64 of 32 random bytes.");
            return new SecretBox(RandomNumberGenerator.GetBytes(KeySize)) { Ephemeral = true };
        }

        byte[] key;
        try { key = Convert.FromBase64String(base64Key.Trim()); }
        catch (FormatException) { throw new InvalidOperationException("Fulfilment:SecretsKey is not valid base64."); }
        if (key.Length != KeySize) throw new InvalidOperationException($"Fulfilment:SecretsKey must be base64 of {KeySize} bytes, it has {key.Length}.");
        return new SecretBox(key);
    }

    public static bool IsSealed(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Seal(string plaintext, string field, Guid allocationId, string deviceId)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_key, TagSize)) aes.Encrypt(nonce, data, cipher, tag, Associated(field, allocationId, deviceId));
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public bool TryOpen(string? stored, string field, Guid allocationId, string deviceId, out string? plaintext)
    {
        plaintext = null;
        if (stored is null || !IsSealed(stored)) return false;
        try
        {
            var raw = Convert.FromBase64String(stored[Prefix.Length..]);
            if (raw.Length < NonceSize + TagSize) return false;
            var data = new byte[raw.Length - NonceSize - TagSize];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(raw.AsSpan(0, NonceSize), raw.AsSpan(NonceSize + TagSize), raw.AsSpan(NonceSize, TagSize), data, Associated(field, allocationId, deviceId));
            plaintext = Encoding.UTF8.GetString(data);
            return true;
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return false; }
    }

    private static byte[] Associated(string field, Guid allocationId, string deviceId) => Encoding.UTF8.GetBytes($"{field}|{allocationId:N}|{deviceId}");

    /// <summary>
    /// Seals secrets that an earlier version stored in the clear. Idempotent: sealed values are left alone, and it runs at start-up, so
    /// after the first start of this version nothing readable is left in the database.
    /// </summary>
    public static async Task<int> SealLegacyAsync(FulfilmentDbContext db, SecretBox box, CancellationToken ct = default)
    {
        var rows = await db.Allocations.Where(a => (a.ClaimToken != null && !a.ClaimToken.StartsWith(Prefix)) || (a.Credential != null && !a.Credential.StartsWith(Prefix))).ToListAsync(ct);
        foreach (var a in rows)
        {
            if (a.ClaimToken is { } t && !IsSealed(t)) a.ClaimToken = box.Seal(t, ClaimTokenField, a.Id, a.DeviceId);
            if (a.Credential is { } c && !IsSealed(c)) a.Credential = box.Seal(c, CredentialField, a.Id, a.DeviceId);
        }
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}