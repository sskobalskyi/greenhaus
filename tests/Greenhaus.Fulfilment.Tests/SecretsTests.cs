using System.Net;
using System.Text.Json.Nodes;
using Greenhaus.Fulfilment.Infrastructure;
using Greenhaus.Fulfilment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Fulfilment.Tests;

/// <summary>The claim token and the broker password of a device in the packing room are sealed in the database.</summary>
[Collection("fulfilment")]
public class SecretsTests(FulfilmentFixture f)
{
    private const string PotKit = "GH-POT-KIT-01";

    private async Task<AllocationEntity> Allocated(Guid order, string customer = "customer-s")
    {
        await f.Confirmed(order, customer, (PotKit, 1));
        return (await f.WaitForAllocations(order, 1))[0];
    }

    private async Task<(HttpStatusCode Status, string Body)> Bundle(Guid order)
    {
        var r = await f.Call("GET", "/orders/{orderId}/allocations", order);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    private Task Overwrite(Guid allocation, string? token = null, string? credential = null) => f.WithDb(async db =>
    {
        var row = await db.Allocations.SingleAsync(a => a.Id == allocation);
        if (token is not null) row.ClaimToken = token;
        if (credential is not null) row.Credential = credential;
        await db.SaveChangesAsync();
        return 0;
    });

    [Fact]
    public async Task The_database_holds_sealed_values_never_the_token_or_the_password()
    {
        var order = Guid.NewGuid();
        var row = await Allocated(order);
        var token = f.Token(row);
        var credential = f.Credential(row);

        Assert.StartsWith(SecretBox.Prefix, row.ClaimToken);
        Assert.StartsWith(SecretBox.Prefix, row.Credential);
        Assert.DoesNotContain(token, row.ClaimToken);
        Assert.DoesNotContain(credential, row.Credential);
        // A value is sealed again with a new nonce each time, so equal secrets would not look equal either.
        Assert.NotEqual(row.ClaimToken, f.Secrets.Seal(token, SecretBox.ClaimTokenField, row.Id, row.DeviceId));

        // The bundle is the same as before the change: the plaintext, to the packing room.
        var bundle = JsonNode.Parse((await Bundle(order)).Body)!.AsArray()[0]!;
        Assert.Equal(token, bundle["claimToken"]!.GetValue<string>());
        Assert.Equal(credential, bundle["credential"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_value_that_was_changed_is_refused_and_neither_the_secret_nor_the_sealed_text_is_sent()
    {
        var order = Guid.NewGuid();
        var row = await Allocated(order);
        var token = f.Token(row);
        var sealedToken = row.ClaimToken!;

        // One character of the ciphertext changed.
        var raw = Convert.FromBase64String(sealedToken[SecretBox.Prefix.Length..]);
        raw[^1] ^= 0x01;
        var tampered = SecretBox.Prefix + Convert.ToBase64String(raw);
        await Overwrite(row.Id, token: tampered);

        var r = await f.Call("GET", "/orders/{orderId}/allocations", order);
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal("secrets_unreadable", JsonNode.Parse(body)!["code"]!.GetValue<string>());
        Assert.DoesNotContain(token, body);
        Assert.DoesNotContain(tampered, body);
        Assert.DoesNotContain(sealedToken, body);

        // The device stays allocated and the original value opens again once it is put back.
        await Overwrite(row.Id, token: sealedToken);
        Assert.Equal(HttpStatusCode.OK, (await Bundle(order)).Status);
    }

    [Fact]
    public async Task A_sealed_value_moved_to_another_device_or_another_column_does_not_open()
    {
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        var a = await Allocated(orderA);
        var b = await Allocated(orderB);

        // B shows A's sealed token: it was sealed for A, so B refuses it.
        await Overwrite(b.Id, token: a.ClaimToken);
        var moved = await Bundle(orderB);
        Assert.Equal(HttpStatusCode.InternalServerError, moved.Status);
        Assert.DoesNotContain(f.Token(a), moved.Body);
        await Overwrite(b.Id, token: b.ClaimToken);

        // The sealed password in the place of the token does not open either: the column is part of what was sealed.
        await Overwrite(a.Id, token: a.Credential);
        Assert.Equal(HttpStatusCode.InternalServerError, (await Bundle(orderA)).Status);
        await Overwrite(a.Id, token: a.ClaimToken);
        Assert.Equal(HttpStatusCode.OK, (await Bundle(orderA)).Status);
    }

    [Fact]
    public async Task Secrets_in_the_clear_from_an_earlier_version_are_sealed_once_at_start_and_not_again()
    {
        var order = Guid.NewGuid();
        var row = await Allocated(order);
        var token = f.Token(row);
        var credential = f.Credential(row);
        await Overwrite(row.Id, token: token, credential: credential);

        var sealedNow = await f.WithDb(db => SecretBox.SealLegacyAsync(db, f.Secrets));
        Assert.True(sealedNow >= 1);
        var after = await f.WithDb(db => db.Allocations.AsNoTracking().SingleAsync(a => a.Id == row.Id));
        Assert.StartsWith(SecretBox.Prefix, after.ClaimToken);
        Assert.Equal(token, f.Token(after));
        Assert.Equal(credential, f.Credential(after));

        var again = await f.WithDb(db => SecretBox.SealLegacyAsync(db, f.Secrets));
        Assert.Equal(0, again);
        Assert.Equal(after.ClaimToken, (await f.WithDb(db => db.Allocations.AsNoTracking().SingleAsync(a => a.Id == row.Id))).ClaimToken);
    }
}