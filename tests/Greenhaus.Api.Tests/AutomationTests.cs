using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Greenhaus.Api.Automation;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Greenhaus.Api.Tests;

/// <summary>Readings the test decides, as the control loop would get them from Pulse.</summary>
public sealed class FakeReadings : IReadingSource
{
    private readonly ConcurrentDictionary<(string, string), ChannelReading> _latest = new();

    public void Set(string device, string channel, double value, DateTimeOffset at) => _latest[(device, channel)] = new ChannelReading(device, channel, value, at);

    public Task<IReadOnlyList<ChannelReading>> LatestAsync(IReadOnlyCollection<ChannelEntity> channels, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChannelReading>>([.. channels.Select(c => _latest.GetValueOrDefault((c.DeviceId, c.ChannelId))).Where(r => r is not null).Select(r => r!)]);
}

public sealed class FakeCommands : ICommandSender
{
    public List<(string Device, string Actuator, int Seconds)> Runs { get; } = [];
    public List<(string Device, string Actuator)> Stops { get; } = [];
    public bool Down { get; set; }

    public Task<Guid> RunAsync(string deviceId, string actuatorId, int seconds, CancellationToken ct)
    {
        if (Down) throw new PulseException(503, "pulse_unavailable", "down");
        Runs.Add((deviceId, actuatorId, seconds));
        return Task.FromResult(Guid.NewGuid());
    }

    public Task<Guid> StopAsync(string deviceId, string actuatorId, CancellationToken ct)
    {
        if (Down) throw new PulseException(503, "pulse_unavailable", "down");
        Stops.Add((deviceId, actuatorId));
        return Task.FromResult(Guid.NewGuid());
    }
}

public sealed class FakeNotifier : IUserNotifier
{
    public ConcurrentQueue<(string User, Guid? Pot, string Kind, IReadOnlyDictionary<string, string> Parameters)> Sent { get; } = new();

    public Task NotifyAsync(string userId, Guid? potId, string kind, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        Sent.Enqueue((userId, potId, kind, parameters));
        return Task.CompletedTask;
    }

    public List<string> KindsFor(string user, Guid? pot = null) => [.. Sent.Where(n => n.User == user && (pot is null || n.Pot == pot)).Select(n => n.Kind)];
}

[Collection("api")]
public class AutomationTests(GreenhausFixture api)
{
    private sealed record Setup(string User, string Pot, string Device);

    private Task<HttpResponseMessage> Call(string user, string method, string template, object? route = null, object? body = null, string? query = null) =>
        api.Client.Call(method, template, route, body, query, GreenhausFixture.As(user));

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return r.StatusCode == HttpStatusCode.NoContent ? new JsonObject() : await api.Client.Node(r);
    }

    /// <summary>A person on a paid tier with a pot, a claimed kit, soil moisture wanted between 40 and 70, a calibrated sensor and the pump on auto.</summary>
    private async Task<Setup> DryPotWithPumpOnAuto(Tier tier = Tier.Gardener, ControlMode pumpMode = ControlMode.Auto)
    {
        var user = GreenhausFixture.NewUser();
        await api.Grant(user, tier);
        var pot = (await Json(Call(user, "POST", "/pots", body: new { name = "Herbs", kind = "pot" }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var device = "loop-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(device), "t");
        await Json(Call(user, "POST", "/claims", body: new { deviceId = device, token = "t", potId = pot }), HttpStatusCode.Created);

        var requirements = await Json(Call(user, "GET", "/pots/{potId}/requirements", new { potId = pot }), HttpStatusCode.OK);
        var soil = requirements["metrics"]!.AsArray().Single(m => m!["metric"]!.GetValue<string>() == "soil_moisture")!;
        soil["enabled"] = true; soil["min"] = 40; soil["max"] = 70; soil["margin"] = 2;
        await Json(Call(user, "PUT", "/pots/{potId}/requirements", new { potId = pot }, requirements), HttpStatusCode.OK);
        await Json(Call(user, "PUT", "/pots/{potId}/channels/{channelId}/calibration", new { potId = pot, channelId = "soil" }, new { dryRaw = 800, wetRaw = 300 }), HttpStatusCode.OK);
        await Json(Call(user, "PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId = pot, actuatorId = "pump" }, new { mode = pumpMode.ToString().ToLowerInvariant() }), HttpStatusCode.OK);
        return new Setup(user, pot, device);
    }

    private sealed class Rig
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        public FakeReadings Readings { get; } = new();
        public FakeCommands Commands { get; } = new();
        public FakeNotifier Notifier { get; } = new();
    }

    /// <summary>One control cycle for one pot with the rig's fake clock, readings, commands and notifier.</summary>
    private Task Cycle(Rig rig, Setup s) => api.WithScope(async sp =>
    {
        var db = sp.GetRequiredService<GreenhausDbContext>();
        var entitlements = new EntitlementService(db, Options.Create(new EntitlementOptions()), rig.Clock);
        var clamp = new TierClamp(db, entitlements, rig.Clock);
        var cycle = new AutomationCycle(db, rig.Readings, rig.Commands, clamp, rig.Notifier, rig.Clock, NullLogger<AutomationCycle>.Instance);
        await cycle.RunPotAsync(Guid.Parse(s.Pot), CancellationToken.None);
        return 0;
    });

    private async Task<JsonArray> Audit(Setup s) => (await Json(Call(s.User, "GET", "/pots/{potId}/audit-log", new { potId = s.Pot }), HttpStatusCode.OK)).AsArray();

    private async Task<JsonNode> Pump(Setup s) =>
        (await Json(Call(s.User, "GET", "/pots/{potId}/actuators", new { potId = s.Pot }), HttpStatusCode.OK)).AsArray().Single(a => a!["id"]!.GetValue<string>() == "pump")!;

    [Fact]
    public async Task A_dry_pot_is_watered_then_left_alone_until_the_interval_has_passed_and_it_dries_again()
    {
        var s = await DryPotWithPumpOnAuto();
        var rig = new Rig();

        rig.Readings.Set(s.Device, "soil", 25, rig.Clock.GetUtcNow());
        await Cycle(rig, s);
        var run = Assert.Single(rig.Commands.Runs);
        Assert.Equal((s.Device, "pump", 10), run);

        // The same cycle again, and the next one, send nothing: it is running, then the interval has not passed.
        await Cycle(rig, s);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Readings.Set(s.Device, "soil", 55, rig.Clock.GetUtcNow());
        await Cycle(rig, s);
        Assert.Single(rig.Commands.Runs);

        // Two hours later the pot is dry again: watered once more.
        rig.Clock.Advance(TimeSpan.FromHours(2));
        rig.Readings.Set(s.Device, "soil", 30, rig.Clock.GetUtcNow());
        await Cycle(rig, s);
        Assert.Equal(2, rig.Commands.Runs.Count);

        var entries = await Audit(s);
        var runs = entries.Where(e => e!["action"]!.GetValue<string>() == "run" && e["actor"]!.GetValue<string>() == "automation").ToList();
        Assert.Equal(2, runs.Count);
        // The log is newest first. The reasons name what the loop saw, and the second run saw a pot that had recovered a little but was dry again.
        Assert.Contains("Soil moisture is 25", runs[^1]!["reason"]!.GetValue<string>());
        Assert.Contains("Soil moisture is 30", runs[0]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Runs_that_change_nothing_block_the_pump_and_tell_the_person_until_they_act()
    {
        var s = await DryPotWithPumpOnAuto();
        var rig = new Rig();

        // The soil stays at 25 however often it is watered, as with an empty tank.
        for (var i = 0; i < 4; i++)
        {
            rig.Readings.Set(s.Device, "soil", 25, rig.Clock.GetUtcNow());
            await Cycle(rig, s);
            rig.Clock.Advance(TimeSpan.FromHours(2));
        }
        Assert.Equal(3, rig.Commands.Runs.Count);

        var pump = await Pump(s);
        Assert.Equal("blocked", pump["state"]!.GetValue<string>());
        var entries = await Audit(s);
        var blocked = Assert.Single(entries, e => e!["action"]!.GetValue<string>() == "blocked");
        Assert.Equal("safety", blocked!["actor"]!.GetValue<string>());
        var notice = Assert.Single(rig.Notifier.Sent, n => n.Kind == "actuator_blocked");
        Assert.Equal("pump", notice.Parameters["actuatorId"]);
        Assert.Equal("dry_run", notice.Parameters["reason"]);

        // Nothing more is sent while it is blocked.
        rig.Readings.Set(s.Device, "soil", 25, rig.Clock.GetUtcNow());
        await Cycle(rig, s);
        Assert.Equal(3, rig.Commands.Runs.Count);

        // A run by hand shows the person looked at it: the block ends.
        await Json(Call(s.User, "POST", "/pots/{potId}/actuators/{actuatorId}/run", new { potId = s.Pot, actuatorId = "pump" }, new { durationSeconds = 5 }), HttpStatusCode.Accepted);
        Assert.Equal("running", (await Pump(s))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notify_mode_only_suggests_and_says_it_once_per_episode()
    {
        var s = await DryPotWithPumpOnAuto(pumpMode: ControlMode.Notify);
        var rig = new Rig();

        for (var i = 0; i < 3; i++)
        {
            rig.Readings.Set(s.Device, "soil", 25, rig.Clock.GetUtcNow());
            await Cycle(rig, s);
            rig.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Empty(rig.Commands.Runs);
        Assert.Single(rig.Notifier.KindsFor(s.User), "automation_suggestion");
        var suggestion = Assert.Single(rig.Notifier.Sent);
        Assert.Equal("soil_moisture", suggestion.Parameters["metric"]);
        Assert.Equal("25", suggestion.Parameters["value"]);
        Assert.Equal("40", suggestion.Parameters["target"]);

        // The soil recovers, which ends the episode, and when it dries again the person hears about it again.
        rig.Readings.Set(s.Device, "soil", 55, rig.Clock.GetUtcNow());
        await Cycle(rig, s);
        rig.Readings.Set(s.Device, "soil", 20, rig.Clock.GetUtcNow());
        await Cycle(rig, s);
        Assert.Equal(2, rig.Notifier.KindsFor(s.User).Count(k => k == "automation_suggestion"));
        Assert.Equal(2, (await Audit(s)).Count(e => e!["action"]!.GetValue<string>() == "suggested"));
    }

    [Fact]
    public async Task A_silent_sensor_means_nothing_is_done_and_it_is_said_once()
    {
        var s = await DryPotWithPumpOnAuto();
        var rig = new Rig();
        rig.Readings.Set(s.Device, "soil", 25, rig.Clock.GetUtcNow().AddHours(-1));

        for (var i = 0; i < 3; i++) { await Cycle(rig, s); rig.Clock.Advance(TimeSpan.FromMinutes(1)); }

        Assert.Empty(rig.Commands.Runs);
        var skipped = Assert.Single((await Audit(s)).Where(e => e!["action"]!.GetValue<string>() == "skipped"));
        Assert.Equal("safety", skipped!["actor"]!.GetValue<string>());
        // A silent sensor is audited once and is not a notification of its own (a silent device is already told as going offline).
        Assert.Empty(rig.Notifier.KindsFor(s.User));

        // Readings are back: the loop works again.
        rig.Readings.Set(s.Device, "soil", 25, rig.Clock.GetUtcNow());
        await Cycle(rig, s);
        Assert.Single(rig.Commands.Runs);
    }

    [Fact]
    public async Task The_kill_switch_stops_the_loop_and_a_pulse_failure_is_retried_next_cycle()
    {
        var s = await DryPotWithPumpOnAuto();
        var rig = new Rig();
        rig.Readings.Set(s.Device, "soil", 25, rig.Clock.GetUtcNow());

        await Json(Call(s.User, "PUT", "/pots/{potId}/kill-switch", new { potId = s.Pot }, new { enabled = true }), HttpStatusCode.OK);
        await Cycle(rig, s);
        Assert.Empty(rig.Commands.Runs);
        await Json(Call(s.User, "PUT", "/pots/{potId}/kill-switch", new { potId = s.Pot }, new { enabled = false }), HttpStatusCode.OK);

        rig.Commands.Down = true;
        await Cycle(rig, s);
        Assert.Empty(rig.Commands.Runs);
        Assert.DoesNotContain(await Audit(s), e => e!["action"]!.GetValue<string>() == "run");

        rig.Commands.Down = false;
        await Cycle(rig, s);
        Assert.Single(rig.Commands.Runs);
    }

    [Fact]
    public async Task A_lapsed_plan_lowers_automation_beyond_the_tier_without_an_event()
    {
        var s = await DryPotWithPumpOnAuto();
        var rig = new Rig();
        // The heater may run automatically on Gardener, and the entitlement ends in an hour.
        await Json(Call(s.User, "PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId = s.Pot, actuatorId = "heater" }, new { mode = "auto" }), HttpStatusCode.OK);
        await api.WithDb(async db =>
        {
            var row = await db.Entitlements.SingleAsync(e => e.UserId == s.User);
            row.ValidUntil = rig.Clock.GetUtcNow().AddHours(1);
            await db.SaveChangesAsync();
            return 0;
        });

        await Cycle(rig, s);
        Assert.Equal("auto", await ModeOf(s, "heater"));

        rig.Clock.Advance(TimeSpan.FromHours(2));
        await Cycle(rig, s);

        Assert.Equal("notify", await ModeOf(s, "heater"));
        Assert.Equal("auto", await ModeOf(s, "pump"));
        var lowered = Assert.Single((await Audit(s)).Where(e => e!["action"]!.GetValue<string>() == "mode_changed" && e["actor"]!.GetValue<string>() == "automation"));
        Assert.Equal("heater", lowered!["actuatorId"]!.GetValue<string>());
        var notice = Assert.Single(rig.Notifier.Sent.Where(n => n.Kind == "plan_changed"));
        Assert.Equal("gardener", notice.Parameters["from"]);
        Assert.Equal("novice", notice.Parameters["to"]);
        Assert.Equal("heater of Herbs", notice.Parameters["lowered"]);

        // Said once: another cycle changes nothing and says nothing more.
        await Cycle(rig, s);
        Assert.Single(rig.Notifier.Sent.Where(n => n.Kind == "plan_changed"));
    }

    private async Task<string> ModeOf(Setup s, string actuator) =>
        (await Json(Call(s.User, "GET", "/pots/{potId}/actuators", new { potId = s.Pot }), HttpStatusCode.OK)).AsArray()
            .Single(a => a!["id"]!.GetValue<string>() == actuator)!["mode"]!.GetValue<string>();

    [Fact]
    public async Task A_billing_event_that_lowers_the_tier_lowers_the_automation_it_no_longer_includes()
    {
        var s = await DryPotWithPumpOnAuto();
        await Json(Call(s.User, "PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId = s.Pot, actuatorId = "heater" }, new { mode = "auto" }), HttpStatusCode.OK);

        await Publish("entitlement.changed.v1", new { userId = s.User, tier = "novice", previousTier = "gardener", reason = "cancelled", at = DateTimeOffset.UtcNow.AddMinutes(1) });

        await Eventually(async () => await ModeOf(s, "heater") == "notify" ? "lowered" : null, "the heater to fall back");
        // Soil moisture automation is part of the free tier, so the pump keeps its mode, and nothing is deleted.
        Assert.Equal("auto", await ModeOf(s, "pump"));
        Assert.Contains(await Audit(s), e => e!["action"]!.GetValue<string>() == "mode_changed" && e["actor"]!.GetValue<string>() == "automation" && e["actuatorId"]!.GetValue<string>() == "heater");
        Assert.Single((await Json(Call(s.User, "GET", "/pots"), HttpStatusCode.OK)).AsArray());
        // One notification tells both that the plan changed and what was switched to notify-only.
        var notices = await Json(Call(s.User, "GET", "/notifications"), HttpStatusCode.OK);
        var change = Assert.Single(notices.AsArray(), n => n!["kind"]!.GetValue<string>() == "plan_changed");
        Assert.Equal("heater of Herbs", change!["params"]!["lowered"]!.GetValue<string>());
    }

    [Fact]
    public async Task Two_instances_looking_at_the_same_dry_pot_send_one_command()
    {
        var s = await DryPotWithPumpOnAuto();
        api.Pulse.Reading(s.Device, "soil", 25, DateTimeOffset.UtcNow);

        await api.WithScope(async sp =>
        {
            var runner = sp.GetRequiredService<AutomationRunner>();
            await Task.WhenAll(runner.RunOnceAsync(CancellationToken.None), runner.RunOnceAsync(CancellationToken.None), runner.RunOnceAsync(CancellationToken.None));
            return 0;
        });

        Assert.Single(api.Pulse.Runs.Where(r => r.Device == s.Device && r.Actuator == "pump"));
        Assert.Single((await Audit(s)).Where(e => e!["action"]!.GetValue<string>() == "run" && e["actor"]!.GetValue<string>() == "automation"));
    }

    // ---- a small publisher and a wait, the way the messaging tests do it ----------------------------------------------

    private async Task Publish(string routingKey, object payload)
    {
        await using var connection = await new ConnectionFactory { HostName = api.RabbitHost, Port = api.RabbitPort, UserName = RabbitMqBuilder.DefaultUsername, Password = RabbitMqBuilder.DefaultPassword }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync("integration.events", ExchangeType.Topic, durable: true);
        var id = Guid.NewGuid();
        var properties = new BasicProperties
        {
            MessageId = id.ToString(), ContentType = "application/json", Persistent = true,
            Headers = new Dictionary<string, object?> { ["messageId"] = id.ToString(), ["occurredAt"] = DateTimeOffset.UtcNow.ToString("O"), ["schemaVersion"] = "1" },
        };
        await channel.BasicPublishAsync("integration.events", routingKey, false, properties, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
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
}
