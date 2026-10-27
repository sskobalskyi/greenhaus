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

    /// <summary>Orderflow's event for a return that was cancelled. The payload is checked against the AsyncAPI document before it is sent.</summary>
    private async Task ReturnCancelled(string customer, Guid order, Guid? messageId = null)
    {
        var payload = new { orderId = order, customerId = customer, cancelledAt = DateTimeOffset.UtcNow };
        billing.Events.VerifyPayload("order.return_cancelled.v1", JsonSerializer.Serialize(payload));
        await Publish("order.return_cancelled.v1", payload, messageId);
    }

    private async Task<int> GrantEvents(Guid order, string status) =>
        (await billing.WithDb(db => db.Outbox.Where(m => m.Type == "grant.status_changed.v1").ToListAsync()))
            .Select(m => JsonNode.Parse(m.Payload)!)
            .Count(b => b["orderId"]!.GetValue<Guid>() == order && b["status"]!.GetValue<string>() == status);

    /// <summary>A later event for another order is handled after the earlier ones, so once its grant is there the earlier ones were seen.</summary>
    private async Task Settled(string user)
    {
        var marker = Guid.NewGuid();
        await Confirmed(user, marker, 30m);
        await GrantOf(user, marker);
    }
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
    [Fact]
    public async Task A_return_cancelled_within_the_grace_period_brings_the_grant_back_to_active_with_its_end_date()
    {
        var user = BillingFixture.NewUser();
        var order = Guid.NewGuid();
        await Confirmed(user, order, 80m);
        var original = await GrantOf(user, order);
        await Listen("entitlement.changed.v1", user);
        await Returned(user, order);
        await Listen("entitlement.changed.v1", user, b => b["reason"]!.GetValue<string>() == "grant_at_risk");
        billing.Clock.Advance(TimeSpan.FromDays(3));

        await ReturnCancelled(user, order);
        var restored = await Eventually(async () => (await Grants(user)).AsArray().FirstOrDefault(g => g!["status"]!.GetValue<string>() == "active"), "the grant to be active again");

        Assert.Equal(original["validUntil"]!.GetValue<DateTimeOffset>(), restored["validUntil"]!.GetValue<DateTimeOffset>());
        Assert.Null(restored["graceEndsAt"]);
        var changed = await Listen("entitlement.changed.v1", user);
        Assert.Equal("gardener", changed.Body["tier"]!.GetValue<string>());
        Assert.Equal("gardener", changed.Body["previousTier"]!.GetValue<string>());
        Assert.Equal("grant_started", changed.Body["reason"]!.GetValue<string>());
        Assert.Equal(original["validUntil"]!.GetValue<DateTimeOffset>(), changed.Body["validUntil"]!.GetValue<DateTimeOffset>());
        Assert.Equal("active", (await Listen("grant.status_changed.v1", user, b => b["status"]!.GetValue<string>() == "active")).Body["status"]!.GetValue<string>());

        // The grace period is over for good: nothing revokes the restored grant.
        billing.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal("active", (await Grants(user))[0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_grant_that_was_revoked_after_the_grace_period_is_not_brought_back()
    {
        var user = BillingFixture.NewUser();
        var order = Guid.NewGuid();
        await Confirmed(user, order, 80m);
        await GrantOf(user, order);
        await Returned(user, order);
        await Eventually(async () => (await Grants(user)).AsArray().FirstOrDefault(g => g!["status"]!.GetValue<string>() == "at_risk"), "the grant to be at risk");
        billing.Clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal("revoked", (await Grants(user))[0]!["status"]!.GetValue<string>());

        await ReturnCancelled(user, order);
        await Settled(user);

        Assert.Equal("revoked", (await Grants(user)).AsArray().First(g => g!["orderId"]!.GetValue<Guid>() == order)!["status"]!.GetValue<string>());
        Assert.Equal(1, await GrantEvents(order, "active"));
    }

    [Fact]
    public async Task A_grant_at_risk_because_of_a_refund_is_not_restored_and_neither_is_one_refunded_after_its_return()
    {
        var user = BillingFixture.NewUser();
        var refunded = Guid.NewGuid();
        var both = Guid.NewGuid();
        await Confirmed(user, refunded, 80m);
        await GrantOf(user, refunded);
        await Confirmed(user, both, 80m);
        await GrantOf(user, both);

        await Refunded(user, refunded);
        await Returned(user, both);
        await Eventually(async () => (await Grants(user)).AsArray().Count(g => g!["status"]!.GetValue<string>() == "at_risk") == 2 ? (await Grants(user)) : null, "both grants to be at risk");
        await Refunded(user, both);
        await ReturnCancelled(user, refunded);
        await ReturnCancelled(user, both);
        await Settled(user);

        var grants = (await Grants(user)).AsArray();
        Assert.Equal("at_risk", grants.First(g => g!["orderId"]!.GetValue<Guid>() == refunded)!["status"]!.GetValue<string>());
        Assert.Equal("at_risk", grants.First(g => g!["orderId"]!.GetValue<Guid>() == both)!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_cancelled_return_for_an_unknown_order_or_a_grant_that_is_not_at_risk_changes_nothing_and_repeats_are_harmless()
    {
        var user = BillingFixture.NewUser();
        var order = Guid.NewGuid();
        await Confirmed(user, order, 80m);
        await GrantOf(user, order);
        var announcedBefore = await GrantEvents(order, "active");

        await ReturnCancelled(user, Guid.NewGuid());
        await ReturnCancelled(user, order);
        await Settled(user);
        Assert.Equal(announcedBefore, await GrantEvents(order, "active"));

        // Returned, cancelled twice (the same message and a second one): restored once.
        await Returned(user, order);
        await Eventually(async () => (await Grants(user)).AsArray().FirstOrDefault(g => g!["status"]!.GetValue<string>() == "at_risk"), "the grant to be at risk");
        var message = Guid.NewGuid();
        await ReturnCancelled(user, order, message);
        await ReturnCancelled(user, order, message);
        await ReturnCancelled(user, order);
        await Settled(user);

        Assert.Equal("active", (await Grants(user)).AsArray().First(g => g!["orderId"]!.GetValue<Guid>() == order)!["status"]!.GetValue<string>());
        Assert.Equal(announcedBefore + 1, await GrantEvents(order, "active"));
    }

    [Fact]
    public async Task Restoring_a_grant_that_is_not_the_best_source_announces_no_entitlement_change()
    {
        var user = BillingFixture.NewUser();
        await Json(Call(user, "POST", "/subscription", new { tier = "druid", paymentMethodToken = "tok" }), HttpStatusCode.Created);
        var order = Guid.NewGuid();
        await Confirmed(user, order, 80m);
        await GrantOf(user, order);
        await Returned(user, order);
        await Eventually(async () => (await Grants(user)).AsArray().FirstOrDefault(g => g!["status"]!.GetValue<string>() == "at_risk"), "the grant to be at risk");

        await ReturnCancelled(user, order);
        await Eventually(async () => (await Grants(user)).AsArray().FirstOrDefault(g => g!["status"]!.GetValue<string>() == "active"), "the grant to be active again");
        await Settled(user);

        // The Druid subscription provides the tier all along, so the user heard about it once, when they subscribed.
        var announced = await billing.WithDb(db => db.Outbox.Where(m => m.Type == "entitlement.changed.v1").ToListAsync());
        Assert.Single(announced, m => m.Payload.Contains(user) && m.Payload.Contains("druid"));
        Assert.DoesNotContain(announced, m => m.Payload.Contains(user) && m.Payload.Contains("grant_at_risk"));
    }
}