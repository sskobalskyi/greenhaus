using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Greenhaus.Fulfilment.Domain;
using Greenhaus.Fulfilment.Persistence;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace Greenhaus.Fulfilment.Tests;

[Collection("fulfilment")]
public class AllocationTests(FulfilmentFixture f)
{
    private const string Starter = "GH-STARTER-01", PotKit = "GH-POT-KIT-01", Greenhouse = "GH-GREENHOUSE-01";

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return await f.Node(r);
    }

    private async Task<Heard> HeardFor(string deviceId) =>
        await f.Eventually(() => Task.FromResult(f.Allocated.FirstOrDefault(h => h.Body.Contains(deviceId))), $"device.allocated.v1 for {deviceId}");

    [Fact]
    public async Task A_confirmed_order_allocates_one_device_per_kit_unit_and_ignores_other_products()
    {
        var order = Guid.NewGuid();
        await f.Confirmed(order, "customer-1", (PotKit, 2), (Greenhouse, 1), ("MUG-01", 3));

        var rows = await f.WaitForAllocations(order, 3);
        await Task.Delay(1000);
        rows = await f.WaitForAllocations(order, 3);
        Assert.Equal(3, rows.Count);
        Assert.Equal([Greenhouse, PotKit, PotKit], rows.Select(r => r.Sku).Order(StringComparer.Ordinal).ToArray());
        Assert.All(rows, r =>
        {
            Assert.Matches("^ghx-[0-9a-f]{12}$", r.DeviceId);
            Assert.Equal("allocated", r.State);
            Assert.Equal("customer-1", r.CustomerId);
        });
        Assert.Equal(3, rows.Select(r => r.DeviceId).Distinct().Count());
    }

    [Fact]
    public async Task Each_device_is_announced_with_hashes_and_capabilities_that_match_the_contract_and_never_a_secret()
    {
        var order = Guid.NewGuid();
        await f.Confirmed(order, "customer-2", (Greenhouse, 1));
        var row = (await f.WaitForAllocations(order, 1))[0];

        var heard = await HeardFor(row.DeviceId);
        f.Events.VerifyPayload(heard.Key, heard.Body);
        f.Events.VerifyHeaders(heard.Key, heard.Headers);

        var payload = JsonNode.Parse(heard.Body)!;
        Assert.Equal(order, payload["orderId"]!.GetValue<Guid>());
        Assert.Equal("customer-2", payload["customerId"]!.GetValue<string>());
        Assert.Equal(Greenhouse, payload["sku"]!.GetValue<string>());
        Assert.Equal("Greenhaus Greenhouse Controller G1", payload["model"]!.GetValue<string>());

        // The device is what its kit says: six channels, four actuators, and a heater that declares its thermal cutoff.
        var capabilities = payload["capabilities"]!;
        Assert.Equal(6, capabilities["channels"]!.AsArray().Count);
        var heater = capabilities["actuators"]!.AsArray().Single(a => a!["kind"]!.GetValue<string>() == "heater")!;
        Assert.True(heater["thermalCutoff"]!.GetValue<bool>());

        // Only hashes travel. The token and the broker password are not in the event, in any form.
        var token = f.Token(row);
        var credential = f.Credential(row);
        Assert.DoesNotContain(token, heard.Body);
        Assert.DoesNotContain(credential, heard.Body);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), payload["claimTokenHash"]!.GetValue<string>());
        Assert.True(Allocator.VerifyCredential(credential, payload["credentialHash"]!.GetValue<string>()));
        Assert.True(payload["claimTokenExpiresAt"]!.GetValue<DateTimeOffset>() > DateTimeOffset.UtcNow.AddDays(80));
    }

    [Fact]
    public async Task A_repeated_message_or_a_second_confirmation_of_the_same_order_allocates_nothing_new()
    {
        var order = Guid.NewGuid();
        var message = Guid.NewGuid();
        JsonObject Payload() => new()
        {
            ["orderId"] = order.ToString(), ["customerId"] = "customer-3",
            ["lines"] = new JsonArray(new JsonObject { ["sku"] = PotKit, ["quantity"] = 2, ["unitPrice"] = 80 }),
            ["total"] = 160, ["currency"] = "USD", ["confirmedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        await f.Publish("order.confirmed.v1", Payload(), message);
        await f.WaitForAllocations(order, 2);
        // The same message again (a redelivery), and the same order under a new message id.
        await f.Publish("order.confirmed.v1", Payload(), message);
        await f.Publish("order.confirmed.v1", Payload());
        // A marker order proves the two above were handled by the time it is seen.
        var marker = Guid.NewGuid();
        await f.Confirmed(marker, "customer-3", (Starter, 1));
        var markerDevice = (await f.WaitForAllocations(marker, 1))[0].DeviceId;
        // Events leave in the order they were written, so once the marker's is heard, an extra one for the order would have been too.
        await HeardFor(markerDevice);

        Assert.Equal(2, (await f.WaitForAllocations(order, 2)).Count);
        Assert.Equal(2, f.Allocated.Count(h => h.Body.Contains(order.ToString())));
    }

    [Fact]
    public async Task The_packing_bundle_shows_the_secrets_of_a_device_in_the_packing_room_only()
    {
        var order = Guid.NewGuid();
        await f.Confirmed(order, "customer-4", (PotKit, 1), (Starter, 1));
        var rows = await f.WaitForAllocations(order, 2);

        var list = await Json(f.Call("GET", "/orders/{orderId}/allocations", order), HttpStatusCode.OK);
        Assert.Equal(2, list.AsArray().Count);
        var pot = list.AsArray().Single(a => a!["sku"]!.GetValue<string>() == PotKit)!;
        var stored = rows.Single(r => r.Sku == PotKit);

        Assert.Equal("allocated", pot["state"]!.GetValue<string>());
        Assert.Equal(f.Token(stored), pot["claimToken"]!.GetValue<string>());
        Assert.Equal(f.Credential(stored), pot["credential"]!.GetValue<string>());
        // The link is what a QR would hold: the claim page of the web app with the device and the token.
        Assert.Equal($"http://localhost:5174/claim?d={stored.DeviceId}&t={Uri.EscapeDataString(f.Token(stored))}", pot["claimUrl"]!.GetValue<string>());

        // Once the device is shipped and Pulse has it, nothing secret is left to show.
        f.Pulse.Register(stored.DeviceId);
        foreach (var r in rows) f.Pulse.Register(r.DeviceId);
        await f.Shipped(order, "customer-4");
        await f.WaitForState(order, "ship_requested", 2);
        await f.SyncPulse();
        await f.WaitForState(order, "shipped", 2);

        var after = await Json(f.Call("GET", "/orders/{orderId}/allocations", order), HttpStatusCode.OK);
        Assert.All(after.AsArray(), a =>
        {
            Assert.Equal("shipped", a!["state"]!.GetValue<string>());
            Assert.Null(a["claimToken"]);
            Assert.Null(a["claimUrl"]);
            Assert.Null(a["credential"]);
            Assert.NotNull(a["shippedAt"]);
        });
        var erased = await f.WithDb(db => db.Allocations.AsNoTracking().Where(a => a.OrderId == order).ToListAsync());
        Assert.All(erased, a => { Assert.Null(a.ClaimToken); Assert.Null(a.Credential); });
    }

    [Fact]
    public async Task Only_fulfilment_staff_may_read_the_bundle_and_an_order_without_kits_has_none()
    {
        var order = Guid.NewGuid();
        await f.Confirmed(order, "customer-5", (Starter, 1));
        await f.WaitForAllocations(order, 1);

        var forbidden = await Json(f.Call("GET", "/orders/{orderId}/allocations", order, roles: "customer"), HttpStatusCode.Forbidden);
        Assert.Equal("forbidden", forbidden["code"]!.GetValue<string>());
        await Json(f.Call("POST", "/orders/{orderId}/allocations/retry", order, roles: "customer"), HttpStatusCode.Forbidden);

        var mugs = Guid.NewGuid();
        await f.Confirmed(mugs, "customer-5", ("MUG-01", 1));
        await Json(f.Call("GET", "/orders/{orderId}/allocations", mugs), HttpStatusCode.NotFound);
        await Json(f.Call("POST", "/orders/{orderId}/allocations/retry", Guid.NewGuid()), HttpStatusCode.NotFound);
        f.Client.AssertCovers();
    }

    [Fact]
    public async Task A_message_that_cannot_be_handled_goes_to_the_dead_letter_queue_and_does_not_block_the_rest()
    {
        await using var channel = await f.OpenChannel();
        await channel.QueueDeclareAsync("fulfilment.events.dead", durable: true, exclusive: false, autoDelete: false);
        await f.Publish("order.confirmed.v1", new JsonObject(), raw: "{ this is not json");

        var order = Guid.NewGuid();
        await f.Confirmed(order, "customer-6", (Starter, 1));
        await f.WaitForAllocations(order, 1);

        var dead = await f.Eventually(async () => await channel.BasicGetAsync("fulfilment.events.dead", true), "the poison message in the dead-letter queue");
        Assert.Contains("this is not json", Encoding.UTF8.GetString(dead.Body.Span));
    }
}