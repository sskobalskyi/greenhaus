using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Greenhaus.Billing.Tests;

/// <summary>
/// Purchase grants through the broker. The tests publish order events the way Orderflow does (raw JSON with the contract headers)
/// and listen on the exchange the way another project would, validating every event billing publishes against the AsyncAPI document.
/// </summary>
[Collection("billing")]
public class GrantTests(BillingFixture billing) : IAsyncLifetime
{
    private IConnection _connection = null!;
    private IChannel _channel = null!;
    private string _spy = null!;

    public async Task InitializeAsync()
    {
        _connection = await new ConnectionFactory { HostName = billing.RabbitHost, Port = billing.RabbitPort, UserName = RabbitMqBuilder.DefaultUsername, Password = RabbitMqBuilder.DefaultPassword }.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();
        await _channel.ExchangeDeclareAsync("integration.events", ExchangeType.Topic, durable: true);
        _spy = (await _channel.QueueDeclareAsync()).QueueName;
        foreach (var key in new[] { "entitlement.changed.v1", "grant.status_changed.v1", "subscription.changed.v1" })
            await _channel.QueueBindAsync(_spy, "integration.events", key);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private Task<HttpResponseMessage> Call(string user, string method, string template, object? body = null) =>
        billing.Client.Call(method, template, null, body, null, BillingFixture.As(user));

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return await billing.Client.Node(r);
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

    private Task Confirmed(string customer, Guid order, decimal total, string currency = "USD", Guid? messageId = null) =>
        Publish("order.confirmed.v1", new
        {
            orderId = order, customerId = customer, lines = new[] { new { sku = "GH-POT-KIT-01", quantity = 1, unitPrice = total } },
            total, currency, confirmedAt = DateTimeOffset.UtcNow,
        }, messageId);

    private Task Returned(string customer, Guid order) =>
        Publish("order.returned.v1", new { orderId = order, customerId = customer, returnedAt = DateTimeOffset.UtcNow, lines = new[] { new { sku = "GH-POT-KIT-01", quantity = 1, unitPrice = 80 } } });

    private Task Refunded(string customer, Guid order) =>
        Publish("order.refunded.v1", new { orderId = order, customerId = customer, amount = 80, currency = "USD", refundedAt = DateTimeOffset.UtcNow, reason = "cancelled" });

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

    private sealed record Heard(string Key, JsonNode Body, Dictionary<string, string?> Headers);

    /// <summary>The next event about this user on the spy queue, checked against the contract on the way.</summary>
    private readonly List<(Heard Event, string Text)> _heard = [];

    private async Task<Heard> Listen(string routingKey, string user, Func<JsonNode, bool>? where = null) =>
        await Eventually(async () =>
        {
            // Events arrive in the order they were staged, not the order a test asks for them, so what was read is kept.
            while (await _channel.BasicGetAsync(_spy, true) is { } message)
            {
                var text = Encoding.UTF8.GetString(message.Body.Span);
                billing.Events.VerifyPayload(message.RoutingKey, text);
                var headers = (message.BasicProperties.Headers ?? new Dictionary<string, object?>())
                    .ToDictionary(h => h.Key, h => (string?)(h.Value is byte[] b ? Encoding.UTF8.GetString(b) : h.Value?.ToString()));
                billing.Events.VerifyHeaders(message.RoutingKey, headers);
                _heard.Add((new Heard(message.RoutingKey, JsonNode.Parse(text)!, headers), text));
            }
            var hit = _heard.FirstOrDefault(h => h.Event.Key == routingKey && h.Text.Contains(user) && (where?.Invoke(h.Event.Body) ?? true));
            if (hit.Event is null) return null;
            _heard.Remove(hit);
            return hit.Event;
        }, $"{routingKey} about {user}");

    private async Task<JsonNode> Grants(string user) => await Json(Call(user, "GET", "/grants"), HttpStatusCode.OK);

    private async Task<JsonNode> GrantOf(string user, Guid order) =>
        await Eventually(async () => (await Grants(user)).AsArray().FirstOrDefault(g => g!["orderId"]!.GetValue<Guid>() == order), "the grant");

    [Fact]
    public async Task A_confirmed_order_of_80_dollars_grants_twelve_months_of_gardener_and_announces_it()
    {
        var user = BillingFixture.NewUser();
        var order = Guid.NewGuid();
        await Confirmed(user, order, 80m);

        var grant = await GrantOf(user, order);
        Assert.Equal("gardener", grant["tier"]!.GetValue<string>());
        Assert.Equal("active", grant["status"]!.GetValue<string>());
        Assert.Equal(360, (grant["validUntil"]!.GetValue<DateTimeOffset>() - grant["validFrom"]!.GetValue<DateTimeOffset>()).TotalDays, 1);

        var changed = await Listen("entitlement.changed.v1", user);
        Assert.Equal("gardener", changed.Body["tier"]!.GetValue<string>());
        Assert.Equal("novice", changed.Body["previousTier"]!.GetValue<string>());
        Assert.Equal("grant_started", changed.Body["reason"]!.GetValue<string>());
        Assert.Equal(grant["validUntil"]!.GetValue<DateTimeOffset>(), changed.Body["validUntil"]!.GetValue<DateTimeOffset>());
        var status = await Listen("grant.status_changed.v1", user);
        Assert.Equal("active", status.Body["status"]!.GetValue<string>());
        Assert.Equal(order, status.Body["orderId"]!.GetValue<Guid>());
    }

    [Theory]
    [InlineData(30, "gardener", 90)]
    [InlineData(150, "druid", 180)]
    public async Task The_thresholds_decide_the_tier_and_the_length(decimal total, string tier, int days)
    {
        var user = BillingFixture.NewUser();
        var order = Guid.NewGuid();
        await Confirmed(user, order, total);

        var grant = await GrantOf(user, order);
        Assert.Equal(tier, grant["tier"]!.GetValue<string>());
        Assert.Equal(days, (grant["validUntil"]!.GetValue<DateTimeOffset>() - grant["validFrom"]!.GetValue<DateTimeOffset>()).TotalDays, 1);
    }

    [Fact]
    public async Task An_order_below_the_lowest_threshold_or_in_another_currency_earns_nothing()
    {
        var user = BillingFixture.NewUser();
        await Confirmed(user, Guid.NewGuid(), 29.99m);
        await Confirmed(user, Guid.NewGuid(), 500m, "EUR");
        var marker = Guid.NewGuid();
        await Confirmed(user, marker, 30m);

        // The marker is handled after the other two, so by now they were seen and ignored.
        await GrantOf(user, marker);
        Assert.Single((await Grants(user)).AsArray());
    }

    [Fact]
    public async Task The_same_order_twice_gives_one_grant_and_one_announcement()
    {
        var user = BillingFixture.NewUser();
        var order = Guid.NewGuid();
        var message = Guid.NewGuid();
        await Confirmed(user, order, 80m, messageId: message);
        await Confirmed(user, order, 80m, messageId: message);
        await Confirmed(user, order, 80m);
        var marker = Guid.NewGuid();
        await Confirmed(user, marker, 30m);
        await GrantOf(user, marker);

        Assert.Equal(2, (await Grants(user)).AsArray().Count);
        var grants = await billing.WithDb(db => db.Outbox.Where(m => m.Type == "grant.status_changed.v1").ToListAsync());
        Assert.Equal(2, grants.Count(m => m.Payload.Contains(user)));
    }

    [Fact]
    public async Task A_second_grant_of_the_same_tier_extends_the_first_and_does_not_change_the_tier()
    {
        var user = BillingFixture.NewUser();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await Confirmed(user, first, 30m);
        var one = await GrantOf(user, first);
        await Listen("entitlement.changed.v1", user);

        await Confirmed(user, second, 30m);
        var two = await GrantOf(user, second);

        Assert.Equal(one["validUntil"]!.GetValue<DateTimeOffset>(), two["validFrom"]!.GetValue<DateTimeOffset>());
        var extended = await Listen("entitlement.changed.v1", user);
        Assert.Equal("gardener", extended.Body["tier"]!.GetValue<string>());
        Assert.Equal("gardener", extended.Body["previousTier"]!.GetValue<string>());
        Assert.Equal(two["validUntil"]!.GetValue<DateTimeOffset>(), extended.Body["validUntil"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task A_return_puts_the_grant_at_risk_for_seven_days_and_then_revokes_it()
    {
        var user = BillingFixture.NewUser();
        var order = Guid.NewGuid();
        await Confirmed(user, order, 80m);
        await GrantOf(user, order);
        await Listen("entitlement.changed.v1", user);

        await Returned(user, order);
        var risky = await Eventually(async () => (await Grants(user)).AsArray().FirstOrDefault(g => g!["status"]!.GetValue<string>() == "at_risk"), "the grant to be at risk");
        var grace = risky["graceEndsAt"]!.GetValue<DateTimeOffset>();
        Assert.Equal(7, (grace - billing.Clock.GetUtcNow()).TotalDays, 1);

        var atRisk = await Listen("entitlement.changed.v1", user);
        Assert.Equal("grant_at_risk", atRisk.Body["reason"]!.GetValue<string>());
        Assert.Equal("gardener", atRisk.Body["tier"]!.GetValue<string>());
        Assert.Equal(grace, atRisk.Body["validUntil"]!.GetValue<DateTimeOffset>());
        Assert.Equal("at_risk", (await Listen("grant.status_changed.v1", user, b => b["status"]!.GetValue<string>() == "at_risk")).Body["status"]!.GetValue<string>());

        // Before the grace ends nothing changes, after it the grant is revoked and the user is back on the free tier.
        billing.Clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal("at_risk", (await Grants(user))[0]!["status"]!.GetValue<string>());
        billing.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal("revoked", (await Grants(user))[0]!["status"]!.GetValue<string>());

        var revoked = await Listen("entitlement.changed.v1", user);
        Assert.Equal("novice", revoked.Body["tier"]!.GetValue<string>());
        Assert.Equal("gardener", revoked.Body["previousTier"]!.GetValue<string>());
        Assert.Equal("grant_revoked", revoked.Body["reason"]!.GetValue<string>());
        Assert.Null(revoked.Body["validUntil"]);
    }

    [Fact]
    public async Task A_refund_of_an_order_that_was_never_returned_also_puts_the_grant_at_risk_and_repeats_change_nothing()
    {
        var user = BillingFixture.NewUser();
        var order = Guid.NewGuid();
        await Confirmed(user, order, 150m);
        await GrantOf(user, order);

        await Refunded(user, order);
        await Eventually(async () => (await Grants(user)).AsArray().FirstOrDefault(g => g!["status"]!.GetValue<string>() == "at_risk"), "the grant to be at risk");
        var graceBefore = (await Grants(user))[0]!["graceEndsAt"]!.GetValue<DateTimeOffset>();

        billing.Clock.Advance(TimeSpan.FromDays(1));
        await Returned(user, order);
        await Refunded(user, order);
        var marker = Guid.NewGuid();
        await Confirmed(user, marker, 30m);
        await GrantOf(user, marker);

        // The grace period did not restart.
        Assert.Equal(graceBefore, (await Grants(user)).AsArray().First(g => g!["orderId"]!.GetValue<Guid>() == order)!["graceEndsAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task A_return_for_an_order_without_a_grant_is_ignored_and_does_not_block_the_queue()
    {
        var user = BillingFixture.NewUser();
        await Returned(user, Guid.NewGuid());
        var order = Guid.NewGuid();
        await Confirmed(user, order, 80m);
        await GrantOf(user, order);
        Assert.Equal("active", (await Grants(user))[0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_paid_subscription_is_announced_and_a_grant_of_a_lower_tier_does_not_change_the_entitlement()
    {
        var user = BillingFixture.NewUser();
        await Json(Call(user, "POST", "/subscription", new { tier = "druid", paymentMethodToken = "tok" }), HttpStatusCode.Created);

        var subscription = await Listen("subscription.changed.v1", user);
        Assert.Equal("active", subscription.Body["status"]!.GetValue<string>());
        var entitlement = await Listen("entitlement.changed.v1", user);
        Assert.Equal("subscribed", entitlement.Body["reason"]!.GetValue<string>());
        Assert.Equal("druid", entitlement.Body["tier"]!.GetValue<string>());

        // A grant of a lower tier than the subscription changes nothing the user could notice, so nothing is announced about it.
        var order = Guid.NewGuid();
        await Confirmed(user, order, 30m);
        await GrantOf(user, order);
        var marker = BillingFixture.NewUser();
        await Json(Call(marker, "POST", "/subscription", new { tier = "druid", paymentMethodToken = "tok" }), HttpStatusCode.Created);
        await Listen("entitlement.changed.v1", marker);

        var announced = await billing.WithDb(db => db.Outbox.Where(m => m.Type == "entitlement.changed.v1").ToListAsync());
        Assert.Single(announced, m => m.Payload.Contains(user));
    }

    [Fact]
    public async Task A_subscription_that_fails_to_renew_and_one_that_ends_are_announced()
    {
        var failing = BillingFixture.NewUser();
        var ending = BillingFixture.NewUser();
        await Json(Call(failing, "POST", "/subscription", new { tier = "druid", paymentMethodToken = "tok-renewal-fails" }), HttpStatusCode.Created);
        await Json(Call(ending, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok" }), HttpStatusCode.Created);
        await Json(Call(ending, "POST", "/subscription/cancel"), HttpStatusCode.OK);

        billing.Clock.Advance(TimeSpan.FromDays(31));
        await Json(Call(failing, "GET", "/subscription"), HttpStatusCode.OK);
        await Json(Call(ending, "GET", "/subscription"), HttpStatusCode.OK);

        Assert.Equal("past_due", (await Listen("subscription.changed.v1", failing, b => b["status"]!.GetValue<string>() == "past_due")).Body["status"]!.GetValue<string>());
        var pastDue = await Listen("entitlement.changed.v1", failing, b => b["reason"]!.GetValue<string>() == "payment_failed");
        Assert.Equal("druid", pastDue.Body["tier"]!.GetValue<string>());

        await Listen("subscription.changed.v1", ending, b => b["status"]!.GetValue<string>() == "cancelled");
        var cancelled = await Listen("entitlement.changed.v1", ending, b => b["reason"]!.GetValue<string>() == "cancelled");
        Assert.Equal("novice", cancelled.Body["tier"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_message_that_cannot_be_handled_goes_to_the_dead_letter_queue_and_does_not_block_the_rest()
    {
        await _channel.QueueDeclareAsync("billing.events.dead", durable: true, exclusive: false, autoDelete: false);
        await Publish("order.confirmed.v1", new { }, raw: "{ this is not json");

        var user = BillingFixture.NewUser();
        var order = Guid.NewGuid();
        await Confirmed(user, order, 80m);
        await GrantOf(user, order);

        var dead = await Eventually(async () => await _channel.BasicGetAsync("billing.events.dead", true), "the poison message in the dead-letter queue");
        Assert.Contains("this is not json", Encoding.UTF8.GetString(dead.Body.Span));
    }
}
