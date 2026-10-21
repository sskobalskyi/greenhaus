using System.Net;
using System.Text.Json.Nodes;

namespace Greenhaus.MockServer.Tests;

public class ContractTests
{
    [Fact]
    public async Task Every_operation_in_the_contract_is_served_and_matches_it()
    {
        using var api = new Api();
        var kitchen = await api.PotNamed("Kitchen herbs");
        var bed = await api.PotNamed("Balcony bed");
        var kitchenId = Api.Id(kitchen);
        var bedId = Api.Id(bed);

        // profile and entitlements
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PATCH", "/me", body: new { displayName = "Fern", locale = "uk" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/me/entitlements")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/tiers")).StatusCode);

        // species
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/species", query: "query=basil")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/species/{speciesId}", new { speciesId = "basil" })).StatusCode);

        // pots
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/pots")).StatusCode);
        var created = await api.Call("POST", "/pots", body: new { name = "Office fern", kind = "pot" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdId = Api.Id(await api.Node(created));
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/pots/{potId}", new { potId = createdId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PATCH", "/pots/{potId}", new { potId = createdId }, new { name = "Office fern 2" })).StatusCode);

        // zones
        var zones = await api.Node(await api.Call("GET", "/pots/{potId}/zones", new { potId = bedId }));
        var zoneId = zones[0]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PATCH", "/pots/{potId}/zones/{zoneId}", new { potId = bedId, zoneId }, new { label = "Tomatoes" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PUT", "/pots/{potId}/view-mode", new { potId = createdId }, new { mode = "zones", gridRows = 2, gridCols = 2 })).StatusCode);

        // plants
        var plant = await api.Call("POST", "/pots/{potId}/plants", new { potId = createdId }, new { speciesId = "boston-fern", nickname = "Fernie" });
        Assert.Equal(HttpStatusCode.Created, plant.StatusCode);
        var plantId = Api.Id(await api.Node(plant));
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/pots/{potId}/plants", new { potId = createdId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PATCH", "/pots/{potId}/plants/{plantId}", new { potId = createdId, plantId }, new { nickname = "Fernando" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Call("DELETE", "/pots/{potId}/plants/{plantId}", new { potId = createdId, plantId })).StatusCode);

        // requirements
        var requirements = await api.Node(await api.Call("GET", "/pots/{potId}/requirements", new { potId = kitchenId }));
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PUT", "/pots/{potId}/requirements", new { potId = kitchenId }, requirements)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/pots/{potId}/requirements/suggestion", new { potId = kitchenId })).StatusCode);

        // status and devices
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/pots/{potId}/status", new { potId = bedId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/pots/{potId}/devices", new { potId = bedId })).StatusCode);

        // controls
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/pots/{potId}/actuators", new { potId = kitchenId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId = kitchenId, actuatorId = "lamp-1" }, new { mode = "notify" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await api.Call("POST", "/pots/{potId}/actuators/{actuatorId}/run", new { potId = kitchenId, actuatorId = "pump-1" }, new { durationSeconds = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PUT", "/pots/{potId}/actuators/{actuatorId}/limits", new { potId = kitchenId, actuatorId = "pump-1" },
            new { maxRunSeconds = 15, minIntervalSeconds = 3600, dailyCapSeconds = 90 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PUT", "/pots/{potId}/kill-switch", new { potId = kitchenId }, new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/pots/{potId}/audit-log", new { potId = kitchenId }, query: "limit=10")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Call("PUT", "/pots/{potId}/channels/{channelId}/calibration", new { potId = bedId, channelId = "soil_moisture_z1" }, new { dryRaw = 3200, wetRaw = 1250 })).StatusCode);

        // care tasks
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/pots/{potId}/care-tasks", new { potId = kitchenId }, query: "state=open")).StatusCode);
        var task = await api.Call("POST", "/pots/{potId}/care-tasks", new { potId = kitchenId }, new { title = "Wipe the leaves", dueAt = DateTimeOffset.UtcNow.AddDays(3) });
        Assert.Equal(HttpStatusCode.Created, task.StatusCode);
        var taskId = Api.Id(await api.Node(task));
        Assert.Equal(HttpStatusCode.OK, (await api.Call("POST", "/pots/{potId}/care-tasks/{taskId}/complete", new { potId = kitchenId, taskId })).StatusCode);

        // claims
        Assert.Equal(HttpStatusCode.OK, (await api.Call("GET", "/claims/preview", query: "deviceId=ghx-demo-0001&token=demo-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await api.Call("POST", "/claims", body: new { deviceId = "ghx-demo-0001", token = "demo-token", potId = kitchenId })).StatusCode);

        // removal last, so earlier calls had something to work with
        Assert.Equal(HttpStatusCode.NoContent, (await api.Call("DELETE", "/pots/{potId}", new { potId = createdId })).StatusCode);

        var missing = api.Contract.Operations().Select(o => o.OperationId).Except(api.Contract.Covered).ToList();
        Assert.True(missing.Count == 0, "Operations in the contract that the mock walk did not exercise: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("novice", 2, "limit_reached")]
    [InlineData("gardener", 6, "limit_reached")]
    public async Task Pot_limit_per_tier_is_enforced_with_a_stable_code(string tier, int limit, string code)
    {
        using var api = new Api();
        HttpResponseMessage? last = null;
        for (var i = 0; i < limit + 1; i++) last = await api.Call("POST", "/pots", body: new { name = $"Pot {i}", kind = "pot" }, tier: tier);

        Assert.Equal(HttpStatusCode.Forbidden, last!.StatusCode);
        var problem = await api.Node(last);
        Assert.Equal(code, problem["code"]!.GetValue<string>());
        Assert.Equal("pots", problem["limitType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Druid_has_unlimited_pots_and_the_limit_is_null()
    {
        using var api = new Api();
        var entitlements = await api.Node(await api.Call("GET", "/me/entitlements", tier: "druid"));
        Assert.Null(entitlements["limits"]!["pots"]);
        Assert.Equal("history", entitlements["limits"]!["aiDataAccess"]!.GetValue<string>());
    }

    [Fact]
    public async Task Zones_view_is_a_paid_feature_and_the_grid_size_depends_on_the_tier()
    {
        using var api = new Api();
        var pot = Api.Id(await api.PotNamed("Window sill"));

        var novice = await api.Call("PUT", "/pots/{potId}/view-mode", new { potId = pot }, new { mode = "zones" }, tier: "novice");
        Assert.Equal(HttpStatusCode.Forbidden, novice.StatusCode);
        Assert.Equal("tier_required", (await api.Node(novice))["code"]!.GetValue<string>());

        var tooBig = await api.Call("PUT", "/pots/{potId}/view-mode", new { potId = pot }, new { mode = "zones", gridRows = 4, gridCols = 4 }, tier: "gardener");
        Assert.Equal(HttpStatusCode.Forbidden, tooBig.StatusCode);
        Assert.Equal("limit_reached", (await api.Node(tooBig))["code"]!.GetValue<string>());

        var druid = await api.Call("PUT", "/pots/{potId}/view-mode", new { potId = pot }, new { mode = "zones", gridRows = 4, gridCols = 4 }, tier: "druid");
        Assert.Equal(HttpStatusCode.OK, druid.StatusCode);
        var zones = await api.Node(await api.Call("GET", "/pots/{potId}/zones", new { potId = pot }));
        Assert.Equal(16, zones.AsArray().Count);
    }

    [Fact]
    public async Task Switching_back_to_simple_requires_a_primary_channel_when_a_metric_has_several()
    {
        using var api = new Api();
        var bed = Api.Id(await api.PotNamed("Balcony bed"));

        var without = await api.Call("PUT", "/pots/{potId}/view-mode", new { potId = bed }, new { mode = "simple" });
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal("primary_channel_required", (await api.Node(without))["code"]!.GetValue<string>());

        var with = await api.Call("PUT", "/pots/{potId}/view-mode", new { potId = bed }, new
        {
            mode = "simple",
            primaryChannels = new[]
            {
                new { metric = "soil_moisture", channelId = "soil_moisture_z1" },
                new { metric = "ph", channelId = "ph_z1" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
        var zones = await api.Node(await api.Call("GET", "/pots/{potId}/zones", new { potId = bed }));
        Assert.Single(zones.AsArray());
    }

    [Fact]
    public async Task Status_is_per_zone_and_the_pot_is_as_bad_as_its_worst_zone()
    {
        using var api = new Api();
        var bed = Api.Id(await api.PotNamed("Balcony bed"));
        var status = await api.Node(await api.Call("GET", "/pots/{potId}/status", new { potId = bed }));

        Assert.Equal("attention", status["state"]!.GetValue<string>());
        var zones = status["zones"]!.AsArray();
        Assert.Equal(4, zones.Count);
        Assert.Equal(2, zones.Count(z => z!["state"]!.GetValue<string>() == "attention"));
        Assert.Contains(zones, z => z!["zoneId"]!.GetValue<string>() == status["worstZoneId"]!.GetValue<string>());

        // No blended value anywhere: every metric belongs to a zone and names its channel.
        foreach (var metric in zones.SelectMany(z => z!["metrics"]!.AsArray()))
            Assert.NotNull(metric!["channelId"]);
    }

    [Fact]
    public async Task Plants_that_disagree_produce_a_conflict_instead_of_a_blended_range()
    {
        using var api = new Api();
        var sill = Api.Id(await api.PotNamed("Window sill"));
        var suggestion = await api.Node(await api.Call("GET", "/pots/{potId}/requirements/suggestion", new { potId = sill }));

        var conflict = Assert.Single(suggestion["conflicts"]!.AsArray());
        Assert.Equal("soil_moisture", conflict!["metric"]!.GetValue<string>());
        Assert.Equal(2, conflict["plants"]!.AsArray().Count);
    }

    [Fact]
    public async Task Auto_mode_is_guarded_by_tier_thermal_cutoff_and_calibration()
    {
        using var api = new Api();
        var kitchen = Api.Id(await api.PotNamed("Kitchen herbs"));
        var bed = Api.Id(await api.PotNamed("Balcony bed"));

        var tier = await api.Call("PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId = bed, actuatorId = "heater-1" }, new { mode = "auto" }, tier: "novice");
        Assert.Equal(HttpStatusCode.Forbidden, tier.StatusCode);
        Assert.Equal("tier_required", (await api.Node(tier))["code"]!.GetValue<string>());

        var cutoff = await api.Call("PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId = bed, actuatorId = "heater-1" }, new { mode = "auto" }, tier: "druid");
        Assert.Equal(HttpStatusCode.BadRequest, cutoff.StatusCode);
        Assert.Equal("thermal_cutoff_required", (await api.Node(cutoff))["code"]!.GetValue<string>());

        var calibration = await api.Call("PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId = bed, actuatorId = "pump-1" }, new { mode = "auto" }, tier: "druid");
        Assert.Equal(HttpStatusCode.BadRequest, calibration.StatusCode);
        Assert.Equal("calibration_required", (await api.Node(calibration))["code"]!.GetValue<string>());

        // Light automation is free, so a novice may enable it on a calibrated pot.
        var free = await api.Call("PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId = kitchen, actuatorId = "lamp-1" }, new { mode = "auto" }, tier: "novice");
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);
    }

    [Fact]
    public async Task Calibrating_every_channel_unlocks_auto_mode_for_the_pump()
    {
        using var api = new Api();
        var bed = Api.Id(await api.PotNamed("Balcony bed"));
        foreach (var channelId in new[] { "soil_moisture_z1", "soil_moisture_z2", "soil_moisture_z3", "soil_moisture_z4" })
            await api.Call("PUT", "/pots/{potId}/channels/{channelId}/calibration", new { potId = bed, channelId }, new { dryRaw = 3200, wetRaw = 1250 });

        var response = await api.Call("PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId = bed, actuatorId = "pump-1" }, new { mode = "auto" }, tier: "druid");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Safety_limits_can_only_be_tightened_and_the_kill_switch_blocks_runs()
    {
        using var api = new Api();
        var kitchen = Api.Id(await api.PotNamed("Kitchen herbs"));

        var loosened = await api.Call("PUT", "/pots/{potId}/actuators/{actuatorId}/limits", new { potId = kitchen, actuatorId = "pump-1" },
            new { maxRunSeconds = 600, minIntervalSeconds = 60, dailyCapSeconds = 5000 });
        Assert.Equal(HttpStatusCode.BadRequest, loosened.StatusCode);
        Assert.Equal("exceeds_hard_limit", (await api.Node(loosened))["code"]!.GetValue<string>());

        await api.Call("PUT", "/pots/{potId}/kill-switch", new { potId = kitchen }, new { enabled = true });
        var run = await api.Call("POST", "/pots/{potId}/actuators/{actuatorId}/run", new { potId = kitchen, actuatorId = "pump-1" }, new { durationSeconds = 5 });
        Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
        Assert.Equal("kill_switch_on", (await api.Node(run))["code"]!.GetValue<string>());

        var log = await api.Node(await api.Call("GET", "/pots/{potId}/audit-log", new { potId = kitchen }));
        Assert.Contains(log.AsArray(), e => e!["actor"]!.GetValue<string>() == "safety" && e["outcome"]!.GetValue<string>() == "refused");
    }

    [Fact]
    public async Task Claim_flow_rejects_unshipped_unknown_and_already_claimed_devices()
    {
        using var api = new Api();
        var pot = Api.Id(await api.PotNamed("Window sill"));

        var preview = await api.Node(await api.Call("GET", "/claims/preview", query: "deviceId=ghx-demo-0002&token=demo-token-2"));
        Assert.False(preview["claimable"]!.GetValue<bool>());
        Assert.Equal("not_shipped", preview["reason"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NotFound, (await api.Call("GET", "/claims/preview", query: "deviceId=ghx-demo-0001&token=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Call("POST", "/claims", body: new { deviceId = "ghx-demo-0003", token = "expired-token", potId = pot })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await api.Call("POST", "/claims", body: new { deviceId = "ghx-demo-0001", token = "demo-token", potId = pot })).StatusCode);
        var again = await api.Call("POST", "/claims", body: new { deviceId = "ghx-demo-0001", token = "demo-token", potId = pot });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_claimed", (await api.Node(again))["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_purchase_grant_at_risk_is_visible_in_the_entitlements()
    {
        using var api = new Api();
        var entitlements = await api.Node(await api.Call("GET", "/me/entitlements", tier: "novice", scenario: "grant-at-risk"));
        var source = Assert.Single(entitlements["sources"]!.AsArray());
        Assert.Equal("at_risk", source!["status"]!.GetValue<string>());
        Assert.NotNull(source["graceEndsAt"]);
    }

    [Fact]
    public async Task Unknown_resources_return_a_documented_problem()
    {
        using var api = new Api();
        var response = await api.Call("GET", "/pots/{potId}", new { potId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task The_validator_itself_rejects_wrong_bodies_and_undocumented_statuses()
    {
        using var api = new Api();

        // A pot without its required fields must not pass as a Pot.
        var wrongBody = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"not-a-uuid\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => api.Contract.Verify(wrongBody, "GET", "/pots/{potId}"));

        var wrongFormat = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"not-a-uuid\",\"name\":\"x\",\"kind\":\"pot\",\"viewMode\":\"simple\",\"killSwitch\":false,\"createdAt\":\"2026-01-01T00:00:00Z\"}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => api.Contract.Verify(wrongFormat, "GET", "/pots/{potId}"));

        var wrongEnum = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"x\",\"kind\":\"shed\",\"viewMode\":\"simple\",\"killSwitch\":false,\"createdAt\":\"2026-01-01T00:00:00Z\"}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => api.Contract.Verify(wrongEnum, "GET", "/pots/{potId}"));

        var undocumented = new HttpResponseMessage(HttpStatusCode.Gone);
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => api.Contract.Verify(undocumented, "GET", "/pots/{potId}"));
    }

    [Fact]
    public async Task Zones_have_no_label_until_the_user_names_them()
    {
        using var api = new Api();
        var bed = Api.Id(await api.PotNamed("Balcony bed"));
        var zones = await api.Node(await api.Call("GET", "/pots/{potId}/zones", new { potId = bed }));
        Assert.All(zones.AsArray(), zone => Assert.Null(zone!["label"]));

        var id = zones[0]!["id"]!.GetValue<string>();
        var renamed = await api.Node(await api.Call("PATCH", "/pots/{potId}/zones/{zoneId}", new { potId = bed, zoneId = id }, new { label = "Tomatoes" }));
        Assert.Equal("Tomatoes", renamed["label"]!.GetValue<string>());
    }
}