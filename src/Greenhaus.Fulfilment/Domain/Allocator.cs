using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Greenhaus.Fulfilment.Domain;

/// <summary>What one kit contains, from data/kit-capabilities.json, in the shape of the Pulse device API.</summary>
public sealed record Kit(string Sku, string Model, JsonObject Capabilities);

public sealed class KitCatalog(IReadOnlyDictionary<string, Kit> kits)
{
    public Kit? Find(string sku) => kits.GetValueOrDefault(sku);
    public int Count => kits.Count;

    public static KitCatalog Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var kits = new Dictionary<string, Kit>();
        foreach (var (sku, node) in root)
        {
            var kit = node!.AsObject();
            var capabilities = new JsonObject
            {
                ["channels"] = kit["channels"]!.DeepClone(),
                ["actuators"] = kit["actuators"]!.DeepClone(),
            };
            kits[sku] = new Kit(sku, kit["model"]!.GetValue<string>(), capabilities);
        }
        return new KitCatalog(kits);
    }
}

/// <summary>The identifiers and secrets of a new device. Pure code, so the formats are tested on their own.</summary>
public static class Allocator
{
    public const int Pbkdf2Iterations = 100_000;

    /// <summary>Letters, digits and dashes only, because the id ends up in topics and URLs. 12 random hex characters make a clash unlikely.</summary>
    public static string NewDeviceId() => "ghx-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>32 random bytes, URL safe. Used for the claim token printed on the device and for the broker password.</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The claim token's hash as the contract wants it: SHA-256 of the UTF-8 bytes, 64 lowercase hex characters.</summary>
    public static string ClaimTokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// A salted PBKDF2-SHA256 hash of the broker password, written `pbkdf2_sha256$iterations$salt$hash` (salt and hash in base64).
    /// The contract calls it opaque: whatever the MQTT broker is set up to verify. This format is the one this service writes.
    /// </summary>
    public static string CredentialHash(string secret, int iterations = Pbkdf2Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2_sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyCredential(string secret, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2_sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(secret, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class AllocationState
{
    /// <summary>Allocated and registered with Pulse by its own consumer. Still in the packing room: the secrets can be read.</summary>
    public const string Allocated = "allocated";
    /// <summary>The order shipped. Pulse has not yet accepted that the device is claimable.</summary>
    public const string ShipRequested = "ship_requested";
    /// <summary>Pulse accepted it: the device is claimable. The secrets are erased.</summary>
    public const string Shipped = "shipped";
    /// <summary>The order was returned or refunded before it shipped. Pulse has not yet retired the device.</summary>
    public const string RetireRequested = "retire_requested";
    public const string Retired = "retired";
    /// <summary>Pulse kept refusing or never answered. Visible so someone can look, and retried on request.</summary>
    public const string PulseFailed = "pulse_failed";
}