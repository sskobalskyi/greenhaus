using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Domain;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Greenhaus.Api.Tests;

[Collection("api")]
public class NotificationTests(GreenhausFixture api) : IAsyncLifetime
{
    private IConnection _connection = null!;
    private IChannel _channel = null!;

    public async Task InitializeAsync()
    {
        _connection = await new ConnectionFactory { HostName = api.RabbitHost, Port = api.RabbitPort, UserName = RabbitMqBuilder.DefaultUsername, Password = RabbitMqBuilder.DefaultPassword }.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();
        await _channel.ExchangeDeclareAsync("integration.events", ExchangeType.Topic, durable: true);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private Task<HttpResponseMessage> Call(string user, string method, string template, object? route = null, object? body = null, string? query = null) =>
        api.Client.Call(method, template, route, body, query, GreenhausFixture.As(user));

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return r.StatusCode == HttpStatusCode.NoContent ? new JsonObject() : await api.Client.Node(r);
    }

    /// <summary>Publishes an event as another project would, after checking its payload against the AsyncAPI document.</summary>
    private async Task Publish(string routingKey, object payload, Guid? messageId = null)
    {
        var json = JsonSerializer.Serialize(payload);
        api.Events.VerifyPayload(routingKey, json);
        var id = messageId ?? Guid.NewGuid();
        var properties = new BasicProperties
        {
            MessageId = id.ToString(), ContentType = "application/json", Persistent = true,
            Headers = new Dictionary<string, object?> { ["messageId"] = id.ToString(), ["occurredAt"] = DateTimeOffset.UtcNow.ToString("O"), ["schemaVersion"] = "1" },
        };
        await _channel.BasicPublishAsync("integration.events", routingKey, false, properties, Encoding.UTF8.GetBytes(json));
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

    private async Task<JsonArray> List(string user, string? query = null) => (await Json(Call(user, "GET", "/notifications", query: query), HttpStatusCode.OK)).AsArray();

    private async Task<JsonNode?> Waiting(string user, string kind, string? query = null) =>
        await Eventually(async () => (await List(user, query)).FirstOrDefault(n => n!["kind"]!.GetValue<string>() == kind), $"a {kind} notification");

    /// <summary>A user with a pot and a claimed kit.</summary>
    private async Task<(string User, string Pot, string Device)> Claimed(Tier? tier = null, string? viewMode = null)
    {
        var user = GreenhausFixture.NewUser();
        if (tier is { } t) await api.Grant(user, t);
        var pot = (await Json(Call(user, "POST", "/pots", body: new { name = "Kit", kind = "pot" }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        if (viewMode == "zones") await Json(Call(user, "PUT", "/pots/{potId}/view-mode", new { potId = pot }, new { mode = "zones", gridRows = 2, gridCols = 2 }), HttpStatusCode.OK);
        var device = "note-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(device), "token-" + device);
        await Json(Call(user, "POST", "/claims", body: new { deviceId = device, token = "token-" + device, potId = pot }), HttpStatusCode.Created);
        return (user, pot, device);
    }

    private static object Alert(string device, string state, string alertId, string channel = "soil", string metric = "soil_moisture", double? value = 31.5) => new
    {
        alertId, deviceId = device, channelId = channel, metric, rule = "threshold", state, direction = "below", value, at = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task An_alert_of_a_claimed_device_notifies_its_owner_once_and_nobody_else()
    {
        var (user, pot, device) = await Claimed();
        var stranger = GreenhausFixture.NewUser();

        var alertId = "alert-" + Guid.NewGuid().ToString("N")[..8];
        await Publish("alert.raised.v1", Alert(device, "raised", alertId));
        var note = await Waiting(user, "alert_raised");
        Assert.Equal(pot, note!["potId"]!.GetValue<string>());
        Assert.Equal("soil_moisture", note["params"]!["metric"]!.GetValue<string>());
        Assert.Equal("below", note["params"]!["direction"]!.GetValue<string>());
        Assert.Equal("31.5", note["params"]!["value"]!.GetValue<string>());

        // The same alert delivered again under another message id is still one notification.
        await Publish("alert.raised.v1", Alert(device, "raised", alertId));
        var second = "alert-" + Guid.NewGuid().ToString("N")[..8];
        await Publish("alert.raised.v1", Alert(device, "raised", second, "temp", "temperature", 9));
        await Eventually(async () => (await List(user)).Count(n => n!["kind"]!.GetValue<string>() == "alert_raised") == 2 ? "two" : null, "the second alert");
        Assert.Equal(2, (await List(user)).Count(n => n!["kind"]!.GetValue<string>() == "alert_raised"));
        Assert.Empty(await List(stranger));

        await Publish("alert.resolved.v1", Alert(device, "resolved", alertId));
        Assert.NotNull(await Waiting(user, "alert_resolved"));
    }

    [Fact]
    public async Task An_alert_in_the_zone_view_says_which_zone_by_position()
    {
        var (user, _, device) = await Claimed(Tier.Gardener, "zones");
        await Publish("alert.raised.v1", Alert(device, "raised", "alert-" + Guid.NewGuid().ToString("N")[..8]));
        var note = await Waiting(user, "alert_raised");
        Assert.Equal("1", note!["params"]!["zoneRow"]!.GetValue<string>());
        Assert.Equal("1", note["params"]!["zoneCol"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_alert_of_a_device_nobody_claimed_is_ignored_without_blocking_the_queue()
    {
        var (user, _, device) = await Claimed();
        await Publish("alert.raised.v1", Alert("nobody-" + Guid.NewGuid().ToString("N")[..8], "raised", "alert-x"));
        await Publish("alert.raised.v1", Alert(device, "raised", "alert-" + Guid.NewGuid().ToString("N")[..8]));
        Assert.Single(await Eventually(async () => { var l = await List(user); return l.Count == 1 ? l : null; }, "the one notification"));
    }

    [Fact]
    public async Task A_controller_going_offline_is_told_once_in_a_burst_and_coming_back_is_told_too()
    {
        var (user, _, device) = await Claimed();
        object Status(string s) => new { deviceId = device, status = s, at = DateTimeOffset.UtcNow, cause = "no_data" };

        await Publish("device.status.v1", Status("offline"));
        await Waiting(user, "device_offline");
        await Publish("device.status.v1", Status("offline"));
        await Publish("device.status.v1", Status("online"));
        await Waiting(user, "device_online");
        await Publish("device.status.v1", Status("offline"));

        // Marker: an alert afterwards proves the repeated offline events were handled by then.
        await Publish("alert.raised.v1", Alert(device, "raised", "alert-" + Guid.NewGuid().ToString("N")[..8]));
        await Waiting(user, "alert_raised");
        var kinds = (await List(user)).Select(n => n!["kind"]!.GetValue<string>()).ToList();
        Assert.Equal(1, kinds.Count(k => k == "device_offline"));
        Assert.Equal(1, kinds.Count(k => k == "device_online"));
    }

    [Fact]
    public async Task A_lower_tier_and_a_grant_at_risk_are_told_but_a_higher_tier_is_not()
    {
        var user = GreenhausFixture.NewUser();
        var now = DateTimeOffset.UtcNow;

        await Publish("entitlement.changed.v1", new { userId = user, tier = "druid", previousTier = "novice", reason = "subscribed", at = now });
        await Eventually(async () => (await Json(Call(user, "GET", "/me/entitlements"), HttpStatusCode.OK))["tier"]!.GetValue<string>() == "druid" ? "yes" : null, "the upgrade");
        Assert.Empty(await List(user));

        var until = now.AddDays(7);
        await Publish("entitlement.changed.v1", new { userId = user, tier = "druid", previousTier = "druid", reason = "grant_at_risk", validUntil = until, at = now.AddMinutes(1) });
        var risk = await Waiting(user, "plan_at_risk");
        Assert.Equal("druid", risk!["params"]!["tier"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(risk["params"]!["until"]?.GetValue<string>()));

        await Publish("entitlement.changed.v1", new { userId = user, tier = "novice", previousTier = "druid", reason = "grant_revoked", at = now.AddMinutes(2) });
        var changed = await Waiting(user, "plan_changed");
        Assert.Equal("druid", changed!["params"]!["from"]!.GetValue<string>());
        Assert.Equal("novice", changed["params"]!["to"]!.GetValue<string>());

        // An old event that arrives late was not applied and says nothing.
        await Publish("entitlement.changed.v1", new { userId = user, tier = "novice", previousTier = "gardener", reason = "cancelled", at = now.AddHours(-1) });
        var marker = GreenhausFixture.NewUser();
        await Publish("entitlement.changed.v1", new { userId = marker, tier = "gardener", previousTier = "novice", reason = "subscribed", at = now });
        await Eventually(async () => await api.WithDb(db => Task.FromResult(db.Entitlements.FirstOrDefault(e => e.UserId == marker))), "the marker event");
        Assert.Equal(1, (await List(user)).Count(n => n!["kind"]!.GetValue<string>() == "plan_changed"));
    }

    [Fact]
    public async Task A_care_task_that_comes_due_is_told_once_to_the_owner()
    {
        var user = GreenhausFixture.NewUser();
        var pot = (await Json(Call(user, "POST", "/pots", body: new { name = "Herbs", kind = "pot" }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        await Json(Call(user, "POST", "/pots/{potId}/care-tasks", new { potId = pot }, new { title = "Turn the pot", dueAt = DateTimeOffset.UtcNow.AddMinutes(-5) }), HttpStatusCode.Created);
        await Json(Call(user, "POST", "/pots/{potId}/care-tasks", new { potId = pot }, new { title = "Later", dueAt = DateTimeOffset.UtcNow.AddDays(3) }), HttpStatusCode.Created);

        await api.WithServices(s => s.GetRequiredService<CareReminders>().RunOnceAsync(default));
        await api.WithServices(s => s.GetRequiredService<CareReminders>().RunOnceAsync(default));

        var due = (await List(user)).Where(n => n!["kind"]!.GetValue<string>() == "care_task_due").ToList();
        var note = Assert.Single(due);
        Assert.Equal("Turn the pot", note!["params"]!["title"]!.GetValue<string>());
        Assert.Equal(pot, note["potId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notifications_are_newest_first_scoped_to_the_caller_and_can_be_read()
    {
        var user = GreenhausFixture.NewUser();
        var other = GreenhausFixture.NewUser();
        async Task Notify(string u, string kind) => await api.WithServices(async s => { await s.GetRequiredService<IUserNotifier>().NotifyAsync(u, null, kind, new Dictionary<string, string> { ["x"] = "1" }, default); return 0; });
        await Notify(user, "care_task_due");
        await Task.Delay(20);
        await Notify(user, "actuator_blocked");
        await Notify(other, "plan_changed");

        var all = await List(user);
        Assert.Equal(["actuator_blocked", "care_task_due"], all.Select(n => n!["kind"]!.GetValue<string>()));
        Assert.Single(await List(user, "limit=1"));

        var id = all[0]!["id"]!.GetValue<string>();
        await Json(Call(other, "POST", "/notifications/{notificationId}/read", new { notificationId = id }), HttpStatusCode.NotFound);
        var read = await Json(Call(user, "POST", "/notifications/{notificationId}/read", new { notificationId = id }), HttpStatusCode.OK);
        Assert.NotNull(read["readAt"]);
        Assert.Equal("care_task_due", Assert.Single(await List(user, "unread=true"))!["kind"]!.GetValue<string>());

        await Json(Call(user, "POST", "/notifications/read-all"), HttpStatusCode.NoContent);
        Assert.Empty(await List(user, "unread=true"));
        // Someone else's notifications were not touched.
        Assert.Single(await List(other, "unread=true"));

        await Json(Call(user, "GET", "/notifications", query: "limit=101"), HttpStatusCode.BadRequest);
    }
}