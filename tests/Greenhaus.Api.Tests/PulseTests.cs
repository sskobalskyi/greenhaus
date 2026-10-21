using System.Net;
using System.Text.Json.Nodes;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Tests;

[Collection("api")]
public class PulseTests(GreenhausFixture api)
{
    private Task<HttpResponseMessage> Call(string user, string method, string template, object? route = null, object? body = null, string? query = null) =>
        api.Client.Call(method, template, route, body, query, GreenhausFixture.As(user));

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return r.StatusCode == HttpStatusCode.NoContent ? new JsonObject() : await api.Client.Node(r);
    }

    /// <summary>A user with a pot and a claimed kit.</summary>
    private async Task<(string User, string Pot, string Device)> Claimed(Tier? tier = null)
    {
        var user = GreenhausFixture.NewUser();
        if (tier is { } t) await api.Grant(user, t);
        var pot = (await Json(Call(user, "POST", "/pots", body: new { name = "Kit", kind = "pot" }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var device = "kit-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(device), "token-" + device);
        await Json(Call(user, "POST", "/claims", body: new { deviceId = device, token = "token-" + device, potId = pot }), HttpStatusCode.Created);
        return (user, pot, device);
    }

    [Fact]
    public async Task Claiming_links_the_controller_its_sensors_and_actuators_and_syncs_alert_rules()
    {
        var (user, pot, device) = await Claimed();

        var devices = await Json(Call(user, "GET", "/pots/{potId}/devices", new { potId = pot }), HttpStatusCode.OK);
        Assert.Equal("online", devices[0]!["status"]!.GetValue<string>());
        Assert.True(devices[0]!["thermalCutoffDeclared"]!.GetValue<bool>());

        var actuators = await Json(Call(user, "GET", "/pots/{potId}/actuators", new { potId = pot }), HttpStatusCode.OK);
        var pump = actuators.AsArray().Single(a => a!["id"]!.GetValue<string>() == "pump")!;
        Assert.Equal("off", pump["mode"]!.GetValue<string>());
        // Settings start cautious and never beyond what the controller itself enforces.
        Assert.Equal(20, pump["limits"]!["maxRunSeconds"]!.GetValue<int>());
        Assert.Equal(60, pump["hardLimits"]!["maxRunSeconds"]!.GetValue<int>());

        // The tank sensor is a safety channel, not a zone reading, so only soil and temperature get rules.
        var rules = api.Pulse.Rules[device];
        Assert.Equal(["soil", "temp"], rules.Select(r => r.ChannelId).Distinct().Order());
        Assert.All(rules, r => Assert.Equal("no_data", r.Kind));

        var queued = await api.WithDb(db => db.Outbox.Where(m => m.Type == "device.claimed.v1").ToListAsync());
        Assert.Contains(queued, m => m.Payload.Contains(device));
    }

    [Fact]
    public async Task A_claimed_or_wrong_token_claim_is_refused_with_pulses_reason()
    {
        var (user, pot, device) = await Claimed();

        var again = await Json(Call(user, "POST", "/claims", body: new { deviceId = device, token = "token-" + device, potId = pot }), HttpStatusCode.Conflict);
        Assert.Equal("already_claimed", again["code"]!.GetValue<string>());

        var other = "kit-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(other), "right");
        var wrong = await Json(Call(user, "POST", "/claims", body: new { deviceId = other, token = "wrong", potId = pot }), HttpStatusCode.Conflict);
        Assert.Equal("token_invalid", wrong["code"]!.GetValue<string>());

        await Json(Call(user, "POST", "/claims", body: new { deviceId = "nobody", token = "x", potId = pot }), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_preview_says_why_a_device_cannot_be_claimed()
    {
        var user = GreenhausFixture.NewUser();
        var id = "kit-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(id, "allocated"), "t");
        var preview = await Json(Call(user, "GET", "/claims/preview", query: $"deviceId={id}&token=t"), HttpStatusCode.OK);
        Assert.False(preview["claimable"]!.GetValue<bool>());
        Assert.Equal("not_shipped", preview["reason"]!.GetValue<string>());

        await Json(Call(user, "GET", "/claims/preview", query: "deviceId=missing&token=t"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Status_compares_each_zone_with_the_requirements_and_names_the_worst()
    {
        var (user, pot, device) = await Claimed();
        var route = new { potId = pot };
        var requirements = await Json(Call(user, "GET", "/pots/{potId}/requirements", route), HttpStatusCode.OK);
        var soil = requirements["metrics"]!.AsArray().Single(m => m!["metric"]!.GetValue<string>() == "soil_moisture")!;
        soil["enabled"] = true; soil["min"] = 40; soil["max"] = 70;
        await Json(Call(user, "PUT", "/pots/{potId}/requirements", route, requirements), HttpStatusCode.OK);

        // Pulse got a threshold for the soil sensor along with the no-data rule.
        Assert.Contains(api.Pulse.Rules[device], r => r.ChannelId == "soil" && r.Kind == "threshold" && r.Min == 40 && r.Max == 70);

        var now = DateTimeOffset.UtcNow;
        api.Pulse.Reading(device, "soil", 25, now);
        api.Pulse.Reading(device, "temp", 21, now);
        var status = await Json(Call(user, "GET", "/pots/{potId}/status", route), HttpStatusCode.OK);
        Assert.Equal("attention", status["state"]!.GetValue<string>());
        Assert.NotNull(status["worstZoneId"]);
        var metrics = status["zones"]![0]!["metrics"]!.AsArray();
        Assert.Equal("below", metrics.Single(m => m!["metric"]!.GetValue<string>() == "soil_moisture")!["direction"]!.GetValue<string>());
        Assert.Single(status["alerts"]!.AsArray());

        api.Pulse.Reading(device, "soil", 55, now);
        var healthy = await Json(Call(user, "GET", "/pots/{potId}/status", route), HttpStatusCode.OK);
        Assert.Equal("ok", healthy["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_silent_sensor_is_offline_not_ok_and_a_missing_pulse_is_unknown()
    {
        var (user, pot, device) = await Claimed();
        var route = new { potId = pot };
        api.Pulse.Reading(device, "soil", 50, DateTimeOffset.UtcNow.AddHours(-2));
        var stale = await Json(Call(user, "GET", "/pots/{potId}/status", route), HttpStatusCode.OK);
        Assert.Equal("unknown", stale["state"]!.GetValue<string>());

        api.Pulse.Down = true;
        try
        {
            var down = await Json(Call(user, "GET", "/pots/{potId}/status", route), HttpStatusCode.OK);
            Assert.Equal("unknown", down["state"]!.GetValue<string>());
            var devices = await Json(Call(user, "GET", "/pots/{potId}/devices", route), HttpStatusCode.OK);
            Assert.Equal("offline", devices[0]!["status"]!.GetValue<string>());
        }
        finally { api.Pulse.Down = false; }
    }

    [Fact]
    public async Task Automatic_watering_needs_a_calibrated_sensor()
    {
        var (user, pot, device) = await Claimed();
        var pump = new { potId = pot, actuatorId = "pump" };

        var refused = await Json(Call(user, "PUT", "/pots/{potId}/actuators/{actuatorId}/mode", pump, new { mode = "auto" }), HttpStatusCode.BadRequest);
        Assert.Equal("calibration_required", refused["code"]!.GetValue<string>());

        await Json(Call(user, "PUT", "/pots/{potId}/channels/{channelId}/calibration", new { potId = pot, channelId = "soil" }, new { dryRaw = 800, wetRaw = 300 }), HttpStatusCode.OK);
        Assert.Equal((800d, 300d), api.Pulse.Calibrations[(device, "soil")]);
        var ok = await Json(Call(user, "PUT", "/pots/{potId}/actuators/{actuatorId}/mode", pump, new { mode = "auto" }), HttpStatusCode.OK);
        Assert.Equal("auto", ok["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Automation_beyond_the_tier_asks_for_the_tier_that_allows_it()
    {
        var (user, pot, _) = await Claimed();
        var heater = new { potId = pot, actuatorId = "heater" };

        var denied = await Json(Call(user, "PUT", "/pots/{potId}/actuators/{actuatorId}/mode", heater, new { mode = "auto" }), HttpStatusCode.Forbidden);
        Assert.Equal("tier_required", denied["code"]!.GetValue<string>());
        Assert.Equal("gardener", denied["requiredTier"]!.GetValue<string>());

        await api.Grant(user, Tier.Gardener);
        // The kit declared a hardware thermal cutoff, so a paid tier may run the heater automatically.
        await Json(Call(user, "PUT", "/pots/{potId}/actuators/{actuatorId}/mode", heater, new { mode = "auto" }), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Manual_runs_are_clamped_and_the_kill_switch_stops_them()
    {
        var (user, pot, device) = await Claimed();
        var pump = new { potId = pot, actuatorId = "pump" };

        await Json(Call(user, "POST", "/pots/{potId}/actuators/{actuatorId}/run", pump, new { durationSeconds = 600 }), HttpStatusCode.Accepted);
        Assert.Contains(api.Pulse.Runs, r => r.Device == device && r.Seconds == 20);

        await Json(Call(user, "PUT", "/pots/{potId}/kill-switch", new { potId = pot }, new { enabled = true }), HttpStatusCode.OK);
        Assert.Contains(api.Pulse.KillSwitches, k => k.Device == device && k.Enabled);
        var blocked = await Json(Call(user, "POST", "/pots/{potId}/actuators/{actuatorId}/run", pump, new { durationSeconds = 5 }), HttpStatusCode.Conflict);
        Assert.Equal("kill_switch_on", blocked["code"]!.GetValue<string>());

        var log = await Json(Call(user, "GET", "/pots/{potId}/audit-log", new { potId = pot }), HttpStatusCode.OK);
        Assert.Contains(log.AsArray(), e => e!["action"]!.GetValue<string>() == "kill_switch");
    }

    [Fact]
    public async Task Safety_limits_can_be_tightened_but_never_beyond_the_controllers_hard_limits()
    {
        var (user, pot, _) = await Claimed();
        var pump = new { potId = pot, actuatorId = "pump" };

        var tighter = await Json(Call(user, "PUT", "/pots/{potId}/actuators/{actuatorId}/limits", pump, new { maxRunSeconds = 5, minIntervalSeconds = 1200, dailyCapSeconds = 30 }), HttpStatusCode.OK);
        Assert.Equal(5, tighter["limits"]!["maxRunSeconds"]!.GetValue<int>());

        var looser = await Json(Call(user, "PUT", "/pots/{potId}/actuators/{actuatorId}/limits", pump, new { maxRunSeconds = 120, minIntervalSeconds = 1200, dailyCapSeconds = 30 }), HttpStatusCode.BadRequest);
        Assert.Equal("limits_beyond_hard", looser["code"]!.GetValue<string>());
    }
}