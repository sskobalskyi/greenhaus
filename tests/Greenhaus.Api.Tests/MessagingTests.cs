using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Greenhaus.Api.Tests;

[Collection("api")]
public class MessagingTests(GreenhausFixture api) : IAsyncLifetime
{
    private IConnection _connection = null!;
    private IChannel _channel = null!;
    private string _spy = null!;

    public async Task InitializeAsync()
    {
        _connection = await new ConnectionFactory { HostName = api.RabbitHost, Port = api.RabbitPort, UserName = RabbitMqBuilder.DefaultUsername, Password = RabbitMqBuilder.DefaultPassword }.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();
        await _channel.ExchangeDeclareAsync("integration.events", ExchangeType.Topic, durable: true);
        // A queue of our own listens to what Greenhaus publishes, the way another project would.
        _spy = (await _channel.QueueDeclareAsync()).QueueName;
        foreach (var key in new[] { "device.claimed.v1", "limit.reached.v1" }) await _channel.QueueBindAsync(_spy, "integration.events", key);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private Task<HttpResponseMessage> Call(string user, string method, string template, object? route = null, object? body = null) =>
        api.Client.Call(method, template, route, body, null, GreenhausFixture.As(user));

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return await api.Client.Node(r);
    }

    private async Task Publish(string routingKey, object payload, Guid? messageId = null, string? raw = null)
    {
        var id = messageId ?? Guid.NewGuid();
        var properties = new BasicProperties
        {
            MessageId = id.ToString(), ContentType = "application/json", Persistent = true,
            Headers = new Dictionary<string, object?> { ["messageId"] = id.ToString(), ["occurredAt"] = DateTimeOffset.UtcNow.ToString("O"), ["schemaVersion"] = "1" },
        };
        await _channel.BasicPublishAsync("integration.events", routingKey, false, properties, Encoding.UTF8.GetBytes(raw ?? JsonSerializer.Serialize(payload)));
    }

    private static async Task<T> Eventually<T>(Func<Task<T?>> probe, string what) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if (await probe() is { } found) return found;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}");
    }

    /// <summary>The next event on the spy queue for a user, with its headers.</summary>
    private sealed record Heard(string Key, string Body, Dictionary<string, string?> Headers);

    private async Task<Heard> Listen(string routingKey, string contains) =>
        await Eventually(async () =>
        {
            while (await _channel.BasicGetAsync(_spy, true) is { } message)
            {
                var body = Encoding.UTF8.GetString(message.Body.Span);
                if (message.RoutingKey == routingKey && body.Contains(contains))
                {
                    var headers = (message.BasicProperties.Headers ?? new Dictionary<string, object?>())
                        .ToDictionary(h => h.Key, h => (string?)(h.Value is byte[] b ? Encoding.UTF8.GetString(b) : h.Value?.ToString()));
                    return new Heard(message.RoutingKey, body, headers);
                }
            }
            return null;
        }, $"{routingKey} about {contains}");

    private async Task<string> NewPot(string user)
    {
        var pot = await Json(Call(user, "POST", "/pots", body: new { name = "Pot", kind = "pot" }), HttpStatusCode.Created);
        return pot["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task A_claim_publishes_device_claimed_with_the_order_the_device_was_allocated_to()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        var device = "evt-" + Guid.NewGuid().ToString("N")[..8];
        var order = Guid.NewGuid();
        api.Pulse.Add(FakePulse.Kit(device), "t");

        await Publish("device.allocated.v1", new { deviceId = device, orderId = order, customerId = user });
        await Eventually(async () => await api.WithDb(db => db.DeviceOrders.FirstOrDefaultAsync(d => d.DeviceId == device)), "the allocation to be stored");
        await Json(Call(user, "POST", "/claims", body: new { deviceId = device, token = "t", potId = pot }), HttpStatusCode.Created);

        var (key, body, headers) = await Listen("device.claimed.v1", device);
        api.Events.VerifyPayload(key, body);
        api.Events.VerifyHeaders(key, headers);
        Assert.Equal(order, JsonNode.Parse(body)!["orderId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task A_device_that_was_not_sold_is_claimed_without_an_order()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        var device = "diy-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(device), "t");
        await Json(Call(user, "POST", "/claims", body: new { deviceId = device, token = "t", potId = pot }), HttpStatusCode.Created);

        var (key, body, _) = await Listen("device.claimed.v1", device);
        api.Events.VerifyPayload(key, body);
        Assert.Null(JsonNode.Parse(body)!["orderId"]);
    }

    [Fact]
    public async Task Hitting_a_tier_limit_publishes_limit_reached_for_the_funnel()
    {
        var user = GreenhausFixture.NewUser();
        await NewPot(user);
        await NewPot(user);
        await Json(Call(user, "POST", "/pots", body: new { name = "Three", kind = "pot" }), HttpStatusCode.Forbidden);

        var (key, body, headers) = await Listen("limit.reached.v1", user);
        api.Events.VerifyPayload(key, body);
        api.Events.VerifyHeaders(key, headers);
        Assert.Equal("pots", JsonNode.Parse(body)!["limitType"]!.GetValue<string>());
        // A published event is marked, so the relay does not send it again.
        await Eventually(async () => (await api.WithDb(db => db.Outbox.Where(m => m.Type == "limit.reached.v1").ToListAsync())).FirstOrDefault(m => m.Payload.Contains(user) && m.PublishedAt is not null), "the event to be marked published");
    }

    [Fact]
    public async Task An_entitlement_event_changes_the_tier_once_and_ignores_duplicates_and_stale_news()
    {
        var user = GreenhausFixture.NewUser();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        async Task<string> Tier() => (await Json(Call(user, "GET", "/me/entitlements"), HttpStatusCode.OK))["tier"]!.GetValue<string>();

        Assert.Equal("novice", await Tier());
        await Publish("entitlement.changed.v1", new { userId = user, tier = "druid", previousTier = "novice", reason = "subscribed", at = now }, id);
        await Eventually(async () => await Tier() == "druid" ? "yes" : null, "the tier to change");

        // The same message again, now claiming something else, changes nothing.
        await Publish("entitlement.changed.v1", new { userId = user, tier = "novice", previousTier = "druid", reason = "cancelled", at = now.AddSeconds(5) }, id);
        // An older event with another id arrives late and must not win either.
        await Publish("entitlement.changed.v1", new { userId = user, tier = "gardener", previousTier = "novice", reason = "subscribed", at = now.AddHours(-1) });
        // A marker event for another user proves both were handled by the time it is seen.
        var marker = GreenhausFixture.NewUser();
        await Publish("entitlement.changed.v1", new { userId = marker, tier = "gardener", previousTier = "novice", reason = "grant_started", at = now });
        await Eventually(async () => (await api.WithDb(db => db.Entitlements.FirstOrDefaultAsync(e => e.UserId == marker))), "the marker event");

        Assert.Equal("druid", await Tier());
        var entitlement = await Json(Call(user, "GET", "/me/entitlements"), HttpStatusCode.OK);
        Assert.Equal("subscription", entitlement["sources"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_grant_that_is_at_risk_is_shown_with_its_deadline()
    {
        var user = GreenhausFixture.NewUser();
        var until = DateTimeOffset.UtcNow.AddDays(7);
        await Publish("entitlement.changed.v1", new { userId = user, tier = "gardener", previousTier = "novice", reason = "grant_at_risk", validUntil = until, at = DateTimeOffset.UtcNow });

        var entitlement = await Eventually(async () =>
        {
            var e = await Json(Call(user, "GET", "/me/entitlements"), HttpStatusCode.OK);
            return e["tier"]!.GetValue<string>() == "gardener" ? e : null;
        }, "the grant to arrive");
        var source = entitlement["sources"]![0]!;
        Assert.Equal("purchase_grant", source["type"]!.GetValue<string>());
        Assert.Equal("at_risk", source["status"]!.GetValue<string>());
        Assert.NotNull(source["graceEndsAt"]);
    }

    [Fact]
    public async Task A_retired_device_leaves_the_pot_and_the_pot_remembers_it()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        var device = "ret-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(device), "t");
        await Json(Call(user, "POST", "/claims", body: new { deviceId = device, token = "t", potId = pot }), HttpStatusCode.Created);

        await Publish("device.retired.v1", new { deviceId = device, retiredAt = DateTimeOffset.UtcNow });
        await Eventually(async () => (await Json(Call(user, "GET", "/pots/{potId}/devices", new { potId = pot }), HttpStatusCode.OK)).AsArray().Count == 0 ? "gone" : null, "the device to leave");

        Assert.Empty((await Json(Call(user, "GET", "/pots/{potId}/actuators", new { potId = pot }), HttpStatusCode.OK)).AsArray());
        var log = await Json(Call(user, "GET", "/pots/{potId}/audit-log", new { potId = pot }), HttpStatusCode.OK);
        Assert.Contains(log.AsArray(), e => e!["action"]!.GetValue<string>() == "device_retired" && e["actor"]!.GetValue<string>() == "automation");
    }

    [Fact]
    public async Task The_result_of_a_run_command_is_written_to_the_audit_log()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        var device = "run-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(device), "t");
        await Json(Call(user, "POST", "/claims", body: new { deviceId = device, token = "t", potId = pot }), HttpStatusCode.Created);
        var receipt = await Json(Call(user, "POST", "/pots/{potId}/actuators/{actuatorId}/run", new { potId = pot, actuatorId = "pump" }, new { durationSeconds = 5 }), HttpStatusCode.Accepted);

        await Publish("command.completed.v1", new { commandId = receipt["commandId"]!.GetValue<Guid>(), deviceId = device, actuatorId = "pump", status = "rejected", reason = "tank empty", finishedAt = DateTimeOffset.UtcNow });
        var entry = await Eventually(async () =>
        {
            var log = await Json(Call(user, "GET", "/pots/{potId}/audit-log", new { potId = pot }), HttpStatusCode.OK);
            return log.AsArray().FirstOrDefault(e => e!["action"]!.GetValue<string>() == "run" && e["outcome"] is not null);
        }, "the outcome");
        Assert.Equal("refused", entry["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_message_that_cannot_be_handled_goes_to_the_dead_letter_queue_and_does_not_block_the_rest()
    {
        await _channel.QueueDeclareAsync("greenhaus.events.dead", durable: true, exclusive: false, autoDelete: false);
        await Publish("entitlement.changed.v1", new { }, raw: "{ this is not json");

        var user = GreenhausFixture.NewUser();
        await Publish("entitlement.changed.v1", new { userId = user, tier = "gardener", previousTier = "novice", reason = "subscribed", at = DateTimeOffset.UtcNow });
        await Eventually(async () => await api.WithDb(db => db.Entitlements.FirstOrDefaultAsync(e => e.UserId == user)), "the next message to be handled");

        var dead = await Eventually(async () => await _channel.BasicGetAsync("greenhaus.events.dead", true), "the poison message in the dead-letter queue");
        Assert.Contains("this is not json", Encoding.UTF8.GetString(dead.Body.Span));
    }
}