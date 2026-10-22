using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Greenhaus.Fulfilment.Domain;
using Greenhaus.Fulfilment.Infrastructure;

namespace Greenhaus.Fulfilment.Tests;

public class AllocatorTests
{
    [Fact]
    public void A_device_id_is_safe_for_topics_and_urls_and_does_not_repeat()
    {
        var ids = Enumerable.Range(0, 500).Select(_ => Allocator.NewDeviceId()).ToList();
        Assert.All(ids, id => Assert.Matches("^[A-Za-z0-9._-]{1,64}$", id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Secrets_are_long_random_and_url_safe()
    {
        var secret = Allocator.NewSecret();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", secret);
        Assert.NotEqual(secret, Allocator.NewSecret());
    }

    [Fact]
    public void The_claim_token_hash_is_the_sha256_hex_of_the_token_as_the_contract_says()
    {
        // The Pulse device API checks exactly this: SHA-256 of the UTF-8 bytes, 64 hexadecimal characters.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Allocator.ClaimTokenHash("abc"));
    }

    [Fact]
    public void A_credential_hash_is_salted_and_verifies_only_the_right_password()
    {
        var one = Allocator.CredentialHash("s3cret", iterations: 1000);
        var two = Allocator.CredentialHash("s3cret", iterations: 1000);
        Assert.NotEqual(one, two);
        Assert.Matches(@"^pbkdf2_sha256\$1000\$[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+$", one);
        Assert.True(Allocator.VerifyCredential("s3cret", one));
        Assert.False(Allocator.VerifyCredential("s3cret ", one));
        Assert.False(Allocator.VerifyCredential("s3cret", "plaintext"));
    }
}

public class KitCatalogTests
{
    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "data", "kit-capabilities.json"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public void Every_product_of_the_shop_catalog_has_a_device_description()
    {
        var kits = KitCatalog.Load(Path.Combine(Repo(), "data", "kit-capabilities.json"));
        var skus = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo(), "data", "shop-catalog.json")))!.AsArray().Select(p => p!["sku"]!.GetValue<string>());
        Assert.All(skus, sku => Assert.NotNull(kits.Find(sku)));
        Assert.Null(kits.Find("MUG-01"));
    }
}

public class HttpPulseDevicesTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Seen.Add(request); return Task.FromResult(respond(request)); }
    }

    private static HttpPulseDevices Make(Stub stub) => new(new HttpClient(stub) { BaseAddress = new Uri("http://pulse/api/pulse/v2/") });

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        new(status) { Content = new StringContent($$"""{"title":"x","status":{{(int)status}},"code":"{{code}}"}""", Encoding.UTF8, "application/problem+json") };

    [Fact]
    public async Task Shipping_and_retiring_post_to_the_device_routes_of_the_pulse_contract()
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var pulse = Make(stub);
        await pulse.ShipAsync("ghx-1", default);
        await pulse.RetireAsync("ghx-1", default);

        Assert.All(stub.Seen, r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.Equal(["/api/pulse/v2/devices/ghx-1/ship", "/api/pulse/v2/devices/ghx-1/retire"], stub.Seen.Select(r => r.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task A_refusal_keeps_pulses_code_and_says_whether_it_is_worth_trying_again()
    {
        var conflict = await Assert.ThrowsAsync<PulseException>(() => Make(new Stub(_ => Problem(HttpStatusCode.Conflict, "invalid_state"))).ShipAsync("d", default));
        Assert.Equal("invalid_state", conflict.Code);
        Assert.False(conflict.Retriable);

        var missing = await Assert.ThrowsAsync<PulseException>(() => Make(new Stub(_ => Problem(HttpStatusCode.NotFound, "not_found"))).ShipAsync("d", default));
        Assert.True(missing.Retriable);

        // A route without a problem document, an empty 5xx and an unreachable Pulse are all worth another try.
        var empty = await Assert.ThrowsAsync<PulseException>(() => Make(new Stub(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))).ShipAsync("d", default));
        Assert.True(empty.Retriable);
        var down = await Assert.ThrowsAsync<PulseException>(() => Make(new Stub(_ => throw new HttpRequestException("refused"))).ShipAsync("d", default));
        Assert.Equal("pulse_unavailable", down.Code);
        Assert.True(down.Retriable);
    }
}