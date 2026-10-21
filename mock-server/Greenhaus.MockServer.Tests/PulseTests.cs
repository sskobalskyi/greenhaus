using Greenhaus.ContractTesting;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Greenhaus.MockServer.Tests;

public class PulseTests
{
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static object Registration(string id = "ghx-new-0001", string token = "new-token", object? channels = null, object? actuators = null) => new
    {
        id,
        model = "Greenhaus Pot Controller P1",
        capabilities = new
        {
            channels = channels ?? new[] { new { id = "soil_moisture_c1", metric = "soil_moisture" }, new { id = "temperature_c1", metric = "temperature" } },
            actuators = actuators ?? new[] { new { id = "pump-1", kind = "pump", hardLimits = new { maxRunSeconds = 30, minIntervalSeconds = 600, dailyCapSeconds = 300 } } },
        },
        credentialHash = "$argon2id$v=19$m=65536,t=3,p=4$salt$hash",
        claimTokenHash = Hash(token),
        claimTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
    };

    private static Dictionary<string, string> Caller(string value) => Api.Headers(("X-Mock-Caller", value));

    private static string Code(JsonNode problem) => problem["code"]!.GetValue<string>();

    private static object Command(Guid? id = null, string type = "run_actuator", int seconds = 10, DateTimeOffset? expires = null) => new
    {
        commandId = id ?? Guid.NewGuid(),
        type,
        actuatorId = "pump-1",
        durationSeconds = seconds,
        expiresAt = expires ?? DateTimeOffset.UtcNow.AddMinutes(5),
    };

    private async Task<string> Status(Api api, string device, Guid id) =>
        (await api.Pulse.Node(await api.Pulse.Call("GET", "/devices/{deviceId}/commands/{commandId}", new { deviceId = device, commandId = id })))["status"]!.GetValue<string>();

    // ---- the whole contract ---------------------------------------------------------------------------------

    [Fact]
    public async Task Every_operation_of_the_pulse_contract_is_served_and_matches_it()
    {
        using var api = new Api();
        var pulse = api.Pulse;

        Assert.Equal(HttpStatusCode.Created, (await pulse.Call("POST", "/devices", body: Registration())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("GET", "/devices", query: "state=allocated")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("GET", "/devices/{deviceId}", new { deviceId = "ghx-new-0001" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("POST", "/devices/{deviceId}/ship", new { deviceId = "ghx-new-0001" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("POST", "/devices/{deviceId}/claim", new { deviceId = "ghx-new-0001" }, new { token = "new-token", ownerId = "demo-user" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("PUT", "/devices/{deviceId}/channels/{channelId}/calibration", new { deviceId = "ghx-new-0001", channelId = "soil_moisture_c1" }, new { dryRaw = 3100, wetRaw = 1300 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("PUT", "/devices/{deviceId}/rules", new { deviceId = "ghx-new-0001" },
            new object[] { new { channelId = "soil_moisture_c1", kind = "threshold", min = 45, max = 65, margin = 2 }, new { channelId = "soil_moisture_c1", kind = "no_data", timeoutSeconds = 60 } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("GET", "/devices/{deviceId}/rules", new { deviceId = "ghx-new-0001" })).StatusCode);
        await api.Mock("POST", "/mock/pulse/devices/ghx-new-0001/reading");
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-new-0001" }, Command(id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("GET", "/devices/{deviceId}/commands", new { deviceId = "ghx-new-0001" }, query: "limit=5")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("GET", "/devices/{deviceId}/commands/{commandId}", new { deviceId = "ghx-new-0001", commandId = id })).StatusCode);
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("GET", "/devices/{deviceId}/channels/{channelId}/series", new { deviceId = "ghx-new-0001", channelId = "soil_moisture_c1" }, query: $"from={from}&resolution=1m")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pulse.Call("POST", "/devices/{deviceId}/retire", new { deviceId = "ghx-new-0001" })).StatusCode);

        pulse.AssertCovers();
    }

    // ---- registration ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Registering_twice_changes_nothing_and_returns_the_first_registration()
    {
        using var api = new Api();
        Assert.Equal(HttpStatusCode.Created, (await api.Pulse.Call("POST", "/devices", body: Registration())).StatusCode);
        var again = await api.Pulse.Call("POST", "/devices", body: Registration(token: "another-token"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("allocated", (await api.Pulse.Node(again))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_registration_reports_every_problem_at_once()
    {
        using var api = new Api();
        var bad = new
        {
            id = "bad id!",
            model = "x",
            capabilities = new
            {
                channels = new[] { new { id = "a", metric = "soil_moisture" }, new { id = "a", metric = "mood" } },
                actuators = new[] { new { id = "pump-1", kind = "pump", hardLimits = new { maxRunSeconds = 500, minIntervalSeconds = 0, dailyCapSeconds = 100 } } },
            },
            credentialHash = "h",
            claimTokenHash = "not-a-hash",
            claimTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        var response = await api.Pulse.Call("POST", "/devices", body: bad);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await api.Pulse.Node(response);
        Assert.Equal("validation_failed", Code(problem));
        var detail = problem["detail"]!.GetValue<string>();
        foreach (var expected in new[] { "id may only contain", "used more than once", "unknown metric", "must not exceed dailyCapSeconds", "SHA-256", "in the future" })
            Assert.Contains(expected, detail);
    }

    [Fact]
    public async Task An_actuator_without_hard_limits_cannot_be_registered()
    {
        using var api = new Api();
        // The defect found in device.allocated.v1: a device that declares no limits must not register.
        var response = await api.Pulse.Call("POST", "/devices", body: Registration(actuators: new[] { new { id = "pump-1", kind = "pump" } }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("hardLimits", (await api.Pulse.Node(response))["detail"]!.GetValue<string>());
    }

    // ---- lifecycle and claim ----------------------------------------------------------------------------------

    [Fact]
    public async Task Shipping_is_idempotent_and_only_allowed_once_from_allocated()
    {
        using var api = new Api();
        var first = await api.Pulse.Call("POST", "/devices/{deviceId}/ship", new { deviceId = "ghx-demo-0002" });
        var second = await api.Pulse.Call("POST", "/devices/{deviceId}/ship", new { deviceId = "ghx-demo-0002" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await api.Pulse.Call("POST", "/devices/{deviceId}/claim", new { deviceId = "ghx-demo-0002" }, new { token = "demo-token-2", ownerId = "demo-user" });
        var late = await api.Pulse.Call("POST", "/devices/{deviceId}/ship", new { deviceId = "ghx-demo-0002" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("invalid_state", Code(await api.Pulse.Node(late)));
    }

    [Fact]
    public async Task A_claim_works_once_and_tells_the_cases_apart()
    {
        using var api = new Api();
        var claim = (string token, string device) => api.Pulse.Call("POST", "/devices/{deviceId}/claim", new { deviceId = device }, new { token, ownerId = "demo-user" });

        // Not shipped yet.
        Assert.Equal("invalid_state", Code(await api.Pulse.Node(await claim("demo-token-2", "ghx-demo-0002"))));
        // A wrong token is reported before an expired one, so the real token is the only one that learns about expiry.
        Assert.Equal("token_invalid", Code(await api.Pulse.Node(await claim("guess", "ghx-demo-0003"))));
        Assert.Equal("token_expired", Code(await api.Pulse.Node(await claim("expired-token", "ghx-demo-0003"))));
        // The right token, once.
        var ok = await api.Pulse.Node(await claim("demo-token", "ghx-demo-0001"));
        Assert.Equal("claimed", ok["state"]!.GetValue<string>());
        Assert.Equal("demo-user", ok["ownerId"]!.GetValue<string>());
        var again = await claim("demo-token", "ghx-demo-0001");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_state", Code(await api.Pulse.Node(again)));
    }

    [Fact]
    public async Task Claim_attempts_are_rate_limited_per_device_and_say_when_to_retry()
    {
        using var api = new Api();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
            last = await api.Pulse.Call("POST", "/devices/{deviceId}/claim", new { deviceId = "ghx-demo-0001" }, new { token = $"guess-{i}", ownerId = "demo-user" });

        Assert.Equal((HttpStatusCode)429, last!.StatusCode);
        Assert.Equal("rate_limited", Code(await api.Pulse.Node(last)));
        Assert.True(last.Headers.TryGetValues("Retry-After", out var retry) && int.Parse(retry.First()) >= 1);

        // Even the right token is refused until the window passes, so guessing cannot be sped up.
        var right = await api.Pulse.Call("POST", "/devices/{deviceId}/claim", new { deviceId = "ghx-demo-0001" }, new { token = "demo-token", ownerId = "demo-user" });
        Assert.Equal((HttpStatusCode)429, right.StatusCode);
    }

    [Fact]
    public async Task The_first_reading_makes_a_claimed_device_active_and_online()
    {
        using var api = new Api();
        await api.Pulse.Call("POST", "/devices/{deviceId}/claim", new { deviceId = "ghx-demo-0001" }, new { token = "demo-token", ownerId = "demo-user" });
        var before = await api.Pulse.Node(await api.Pulse.Call("GET", "/devices/{deviceId}", new { deviceId = "ghx-demo-0001" }));
        Assert.Equal("unknown", before["connection"]!.GetValue<string>());

        await api.Mock("POST", "/mock/pulse/devices/ghx-demo-0001/reading");
        var after = await api.Pulse.Node(await api.Pulse.Call("GET", "/devices/{deviceId}", new { deviceId = "ghx-demo-0001" }));
        Assert.Equal("active", after["state"]!.GetValue<string>());
        Assert.Equal("online", after["connection"]!.GetValue<string>());

        // Silence turns a device offline after the no-data timeout.
        await api.Mock("POST", "/mock/pulse/devices/ghx-demo-0001/silence");
        var silent = await api.Pulse.Node(await api.Pulse.Call("GET", "/devices/{deviceId}", new { deviceId = "ghx-demo-0001" }));
        Assert.Equal("offline", silent["connection"]!.GetValue<string>());
    }

    [Fact]
    public async Task Retiring_is_idempotent_and_ends_the_claim_token_and_the_commands()
    {
        using var api = new Api();
        Assert.Equal(HttpStatusCode.OK, (await api.Pulse.Call("POST", "/devices/{deviceId}/retire", new { deviceId = "ghx-demo-0001" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Pulse.Call("POST", "/devices/{deviceId}/retire", new { deviceId = "ghx-demo-0001" })).StatusCode);

        var claim = await api.Pulse.Call("POST", "/devices/{deviceId}/claim", new { deviceId = "ghx-demo-0001" }, new { token = "demo-token", ownerId = "demo-user" });
        Assert.Equal("invalid_state", Code(await api.Pulse.Node(claim)));

        // A retired device takes no commands.
        var command = await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-demo-0001" }, Command());
        Assert.Equal(HttpStatusCode.Conflict, command.StatusCode);
    }

    // ---- who may see and do what -------------------------------------------------------------------------------

    [Fact]
    public async Task A_user_sees_only_their_own_devices_and_cannot_use_service_operations()
    {
        using var api = new Api();
        var user = Caller("user:demo-user");

        var list = await api.Pulse.Node(await api.Pulse.Call("GET", "/devices", headers: user));
        Assert.Equal(new[] { "ghx-bed-balcony", "ghx-pot-kitchen" }, list.AsArray().Select(d => d!["id"]!.GetValue<string>()).ToArray());

        // Somebody else's device does not exist for them, and neither does a device nobody owns yet.
        Assert.Equal(HttpStatusCode.NotFound, (await api.Pulse.Call("GET", "/devices/{deviceId}", new { deviceId = "ghx-demo-0001" }, headers: user)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Pulse.Call("GET", "/devices/{deviceId}", new { deviceId = "ghx-pot-kitchen" }, headers: user)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Pulse.Call("GET", "/devices/{deviceId}", new { deviceId = "ghx-pot-kitchen" }, headers: Caller("user:somebody-else"))).StatusCode);

        // Registry operations are for the service.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Pulse.Call("POST", "/devices", body: Registration(), headers: user)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Pulse.Call("POST", "/devices/{deviceId}/retire", new { deviceId = "ghx-pot-kitchen" }, headers: user)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_state_filter_is_a_documented_problem()
    {
        using var api = new Api();
        var response = await api.Pulse.Call("GET", "/devices", query: "state=sleeping");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_state_filter", Code(await api.Pulse.Node(response)));
    }

    // ---- rules and calibration ----------------------------------------------------------------------------------

    [Fact]
    public async Task Rules_must_name_a_channel_of_the_device_and_make_sense()
    {
        using var api = new Api();
        var bad = await api.Pulse.Call("PUT", "/devices/{deviceId}/rules", new { deviceId = "ghx-pot-kitchen" }, new object[]
        {
            new { channelId = "nope", kind = "threshold", min = 1 },
            new { channelId = "soil_moisture_z0", kind = "threshold" },
            new { channelId = "soil_moisture_z0", kind = "threshold", min = 70, max = 40 },
            new { channelId = "soil_moisture_z0", kind = "no_data", timeoutSeconds = 5 },
            new { channelId = "soil_moisture_z0", kind = "mystery" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var detail = (await api.Pulse.Node(bad))["detail"]!.GetValue<string>();
        foreach (var expected in new[] { "does not exist", "needs min or max", "min is above max", "at least 10", "unknown kind" }) Assert.Contains(expected, detail);
    }

    [Fact]
    public async Task Calibration_needs_two_different_values_and_an_existing_channel()
    {
        using var api = new Api();
        var same = await api.Pulse.Call("PUT", "/devices/{deviceId}/channels/{channelId}/calibration", new { deviceId = "ghx-pot-kitchen", channelId = "soil_moisture_z0" }, new { dryRaw = 2000, wetRaw = 2000 });
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        var missing = await api.Pulse.Call("PUT", "/devices/{deviceId}/channels/{channelId}/calibration", new { deviceId = "ghx-pot-kitchen", channelId = "nope" }, new { dryRaw = 3000, wetRaw = 1000 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---- commands -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_command_to_an_online_device_is_sent_and_then_carried_out()
    {
        using var api = new Api();
        var id = Guid.NewGuid();
        var accepted = await api.Pulse.Node(await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(id)));
        Assert.Equal("sent", accepted["status"]!.GetValue<string>());

        await api.Advance(0.0001); // a few seconds
        Assert.Equal("completed", await Status(api, "ghx-pot-kitchen", id));
    }

    [Fact]
    public async Task The_controller_refuses_what_exceeds_its_own_hard_limits_whatever_the_cloud_asks()
    {
        using var api = new Api();
        var id = Guid.NewGuid();
        await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(id, seconds: 600)); // pump allows 60
        await api.Advance(0.0001);
        var command = await api.Pulse.Node(await api.Pulse.Call("GET", "/devices/{deviceId}/commands/{commandId}", new { deviceId = "ghx-pot-kitchen", commandId = id }));
        Assert.Equal("rejected", command["status"]!.GetValue<string>());
        Assert.Equal("exceeds_hard_limit", command["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_second_run_too_soon_is_refused_and_the_kill_switch_stops_everything()
    {
        using var api = new Api();
        var first = Guid.NewGuid();
        await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(first));
        await api.Advance(0.0001);
        Assert.Equal("completed", await Status(api, "ghx-pot-kitchen", first));

        var soon = Guid.NewGuid();
        await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(soon));
        await api.Advance(0.0001);
        var tooSoon = await api.Pulse.Node(await api.Pulse.Call("GET", "/devices/{deviceId}/commands/{commandId}", new { deviceId = "ghx-pot-kitchen", commandId = soon }));
        Assert.Equal("min_interval", tooSoon["reason"]!.GetValue<string>());

        await api.Advance(1); // well past the minimum interval
        await api.Mock("POST", "/mock/pulse/devices/ghx-pot-kitchen/reading"); // the device keeps reporting, so it is still online
        var kill = Guid.NewGuid();
        await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, new { commandId = kill, type = "set_kill_switch", actuatorId = "pump-1", enabled = true, expiresAt = DateTimeOffset.UtcNow.AddDays(3) });
        await api.Advance(0.0001);
        Assert.Equal("completed", await Status(api, "ghx-pot-kitchen", kill));

        var blocked = Guid.NewGuid();
        await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(blocked, expires: DateTimeOffset.UtcNow.AddDays(3)));
        await api.Advance(0.0001);
        var refused = await api.Pulse.Node(await api.Pulse.Call("GET", "/devices/{deviceId}/commands/{commandId}", new { deviceId = "ghx-pot-kitchen", commandId = blocked }));
        Assert.Equal("kill_switch", refused["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_command_is_idempotent_on_its_id_and_an_id_cannot_be_reused_for_something_else()
    {
        using var api = new Api();
        var id = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        var first = await api.Pulse.Node(await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(id, expires: expires)));
        var again = await api.Pulse.Node(await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(id, expires: expires)));
        Assert.Equal(first["createdAt"]!.GetValue<string>(), again["createdAt"]!.GetValue<string>());
        Assert.Single((await api.Pulse.Node(await api.Pulse.Call("GET", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }))).AsArray());

        var different = await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(id, seconds: 20, expires: expires));
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
    }

    [Fact]
    public async Task A_command_that_is_already_expired_or_names_an_unknown_actuator_is_refused()
    {
        using var api = new Api();
        var expired = await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(expires: DateTimeOffset.UtcNow.AddSeconds(-5)));
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal("command_expired", Code(await api.Pulse.Node(expired)));

        var unknown = await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" },
            new { commandId = Guid.NewGuid(), type = "run_actuator", actuatorId = "nope", durationSeconds = 5, expiresAt = DateTimeOffset.UtcNow.AddMinutes(1) });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task A_command_for_an_offline_device_waits_and_expires_if_the_device_never_returns()
    {
        using var api = new Api();
        await api.Mock("POST", "/mock/pulse/devices/ghx-pot-kitchen/silence");
        var id = Guid.NewGuid();
        var queued = await api.Pulse.Node(await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(id, expires: DateTimeOffset.UtcNow.AddDays(1))));
        Assert.Equal("queued", queued["status"]!.GetValue<string>());

        await api.Advance(2);
        Assert.Equal("expired", await Status(api, "ghx-pot-kitchen", id));
    }

    [Fact]
    public async Task A_command_for_an_offline_device_runs_when_it_comes_back_before_it_expires()
    {
        using var api = new Api();
        await api.Mock("POST", "/mock/pulse/devices/ghx-pot-kitchen/silence");
        var id = Guid.NewGuid();
        await api.Pulse.Call("POST", "/devices/{deviceId}/commands", new { deviceId = "ghx-pot-kitchen" }, Command(id, expires: DateTimeOffset.UtcNow.AddDays(1)));

        await api.Mock("POST", "/mock/pulse/devices/ghx-pot-kitchen/reading");
        await api.Advance(0.0001);
        Assert.Equal("completed", await Status(api, "ghx-pot-kitchen", id));
    }

    // ---- series -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Series_windows_are_limited_per_resolution_and_a_long_raw_series_says_it_was_cut()
    {
        using var api = new Api();
        string Query(TimeSpan back, string resolution) => $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.Subtract(back).ToString("O"))}&resolution={resolution}";
        var route = new { deviceId = "ghx-pot-kitchen", channelId = "soil_moisture_z0" };
        const string template = "/devices/{deviceId}/channels/{channelId}/series";

        var tooLong = await api.Pulse.Call("GET", template, route, query: Query(TimeSpan.FromHours(7), "raw"));
        Assert.Equal("window_too_large", Code(await api.Pulse.Node(tooLong)));

        var exactly = await api.Pulse.Call("GET", template, route, query: Query(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(30), "raw"));
        Assert.Equal(HttpStatusCode.OK, exactly.StatusCode);
        Assert.Equal("true", exactly.Headers.GetValues("X-Pulse-Truncated").Single());
        Assert.Equal(10_000, (await api.Pulse.Node(exactly)).AsArray().Count);

        var short_ = await api.Pulse.Call("GET", template, route, query: Query(TimeSpan.FromMinutes(5), "raw"));
        Assert.False(short_.Headers.Contains("X-Pulse-Truncated"));

        var minutes = (await api.Pulse.Node(await api.Pulse.Call("GET", template, route, query: Query(TimeSpan.FromHours(2), "1m")))).AsArray();
        Assert.All(minutes, p => Assert.True(p!["min"]!.GetValue<double>() <= p["value"]!.GetValue<double>() && p["value"]!.GetValue<double>() <= p["max"]!.GetValue<double>()));

        var hours = (await api.Pulse.Node(await api.Pulse.Call("GET", template, route, query: Query(TimeSpan.FromDays(30), "1h")))).AsArray();
        Assert.InRange(hours.Count, 719, 722);
    }

    [Fact]
    public async Task A_series_request_that_makes_no_sense_or_names_no_channel_is_refused()
    {
        using var api = new Api();
        var route = new { deviceId = "ghx-pot-kitchen", channelId = "soil_moisture_z0" };
        const string template = "/devices/{deviceId}/channels/{channelId}/series";
        var now = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"));

        Assert.Equal("invalid_range", Code(await api.Pulse.Node(await api.Pulse.Call("GET", template, route, query: "resolution=raw"))));
        Assert.Equal("invalid_range", Code(await api.Pulse.Node(await api.Pulse.Call("GET", template, route, query: $"from={now}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}"))));
        Assert.Equal("invalid_resolution", Code(await api.Pulse.Node(await api.Pulse.Call("GET", template, route, query: $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"))}&resolution=1d"))));
        Assert.Equal(HttpStatusCode.NotFound, (await api.Pulse.Call("GET", template, new { deviceId = "ghx-pot-kitchen", channelId = "nope" }, query: $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"))}")).StatusCode);
    }

    [Fact]
    public async Task The_pulse_validator_rejects_a_wrong_device()
    {
        using var api = new Api();
        // A device without its required fields must not pass as a Device.
        var wrong = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"x\"}", Encoding.UTF8, "application/json") };
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => api.Pulse.Contract.Verify(wrong, "GET", "/devices/{deviceId}"));
    }
}
