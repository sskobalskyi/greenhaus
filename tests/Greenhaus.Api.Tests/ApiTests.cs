using System.Net;
using System.Text.Json.Nodes;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Tests;

[Collection("api")]
public class ApiTests(GreenhausFixture api)
{
    private Task<HttpResponseMessage> Call(string user, string method, string template, object? route = null, object? body = null, string? query = null) =>
        api.Client.Call(method, template, route, body, query, GreenhausFixture.As(user));

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return r.StatusCode == HttpStatusCode.NoContent ? new JsonObject() : await api.Client.Node(r);
    }

    private async Task<string> NewPot(string user, string name = "Kitchen")
    {
        var pot = await Json(Call(user, "POST", "/pots", body: new { name, kind = "pot" }), HttpStatusCode.Created);
        return pot["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task A_walk_through_the_service_matches_the_contract()
    {
        var user = GreenhausFixture.NewUser();
        await api.Grant(user, Tier.Gardener);

        Assert.Equal("Gardener", (await Json(Call(user, "GET", "/me"), HttpStatusCode.OK))["displayName"] is null ? "" : "Gardener");
        await Json(Call(user, "PATCH", "/me", body: new { locale = "uk" }), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/me/entitlements"), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/tiers"), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/species", query: "query=basil"), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/species/{speciesId}", new { speciesId = "basil" }), HttpStatusCode.OK);

        var potId = await NewPot(user);
        var route = new { potId };
        await Json(Call(user, "GET", "/pots"), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/pots/{potId}", route), HttpStatusCode.OK);
        await Json(Call(user, "PATCH", "/pots/{potId}", route, new { name = "Window" }), HttpStatusCode.OK);

        var plant = await Json(Call(user, "POST", "/pots/{potId}/plants", route, new { speciesId = "basil", nickname = "Basil" }), HttpStatusCode.Created);
        await Json(Call(user, "GET", "/pots/{potId}/plants", route), HttpStatusCode.OK);
        await Json(Call(user, "PATCH", "/pots/{potId}/plants/{plantId}", new { potId, plantId = plant["id"]!.GetValue<string>() }, new { nickname = "Genovese" }), HttpStatusCode.OK);

        var suggestion = await Json(Call(user, "GET", "/pots/{potId}/requirements/suggestion", route), HttpStatusCode.OK);
        await Json(Call(user, "PUT", "/pots/{potId}/requirements", route, suggestion["requirements"]!.DeepClone()), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/pots/{potId}/requirements", route), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/pots/{potId}/status", route), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/pots/{potId}/devices", route), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/pots/{potId}/actuators", route), HttpStatusCode.OK);

        var tasks = await Json(Call(user, "GET", "/pots/{potId}/care-tasks", route), HttpStatusCode.OK);
        await Json(Call(user, "POST", "/pots/{potId}/care-tasks", route, new { title = "Turn the pot", dueAt = "2030-01-01T00:00:00Z" }), HttpStatusCode.Created);
        await Json(Call(user, "POST", "/pots/{potId}/care-tasks/{taskId}/complete", new { potId, taskId = tasks[0]!["id"]!.GetValue<string>() }), HttpStatusCode.OK);

        var zonesView = await Json(Call(user, "PUT", "/pots/{potId}/view-mode", route, new { mode = "zones", gridRows = 2, gridCols = 2 }), HttpStatusCode.OK);
        Assert.Equal("zones", zonesView["viewMode"]!.GetValue<string>());
        var zones = await Json(Call(user, "GET", "/pots/{potId}/zones", route), HttpStatusCode.OK);
        Assert.Equal(4, zones.AsArray().Count);
        await Json(Call(user, "PATCH", "/pots/{potId}/zones/{zoneId}", new { potId, zoneId = zones[0]!["id"]!.GetValue<string>() }, new { label = "Window side" }), HttpStatusCode.OK);
        await Json(Call(user, "PUT", "/pots/{potId}/view-mode", route, new { mode = "simple" }), HttpStatusCode.OK);

        await Json(Call(user, "PUT", "/pots/{potId}/kill-switch", route, new { enabled = true }), HttpStatusCode.OK);
        await Json(Call(user, "GET", "/pots/{potId}/audit-log", route), HttpStatusCode.OK);
        await Json(Call(user, "PUT", "/pots/{potId}/kill-switch", route, new { enabled = false }), HttpStatusCode.OK);

        var deviceId = "walk-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(deviceId), "secret-token");
        await Json(Call(user, "GET", "/claims/preview", query: $"deviceId={deviceId}&token=secret-token"), HttpStatusCode.OK);
        await Json(Call(user, "POST", "/claims", body: new { deviceId, token = "secret-token", potId }), HttpStatusCode.Created);
        await Json(Call(user, "GET", "/pots/{potId}/devices", route), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await Call(user, "POST", "/pots/{potId}/assistant/ask", route, new { question = "Is my basil ok?" })).StatusCode);
        await Json(Call(user, "PUT", "/pots/{potId}/actuators/{actuatorId}/mode", new { potId, actuatorId = "heater" }, new { mode = "notify" }), HttpStatusCode.OK);
        await Json(Call(user, "POST", "/pots/{potId}/actuators/{actuatorId}/run", new { potId, actuatorId = "pump" }, new { durationSeconds = 10 }), HttpStatusCode.Accepted);
        await Json(Call(user, "PUT", "/pots/{potId}/actuators/{actuatorId}/limits", new { potId, actuatorId = "pump" }, new { maxRunSeconds = 10, minIntervalSeconds = 900, dailyCapSeconds = 60 }), HttpStatusCode.OK);
        await Json(Call(user, "PUT", "/pots/{potId}/channels/{channelId}/calibration", new { potId, channelId = "soil" }, new { dryRaw = 800, wetRaw = 300 }), HttpStatusCode.OK);

        await Json(Call(user, "DELETE", "/pots/{potId}/plants/{plantId}", new { potId, plantId = plant["id"]!.GetValue<string>() }), HttpStatusCode.NoContent);
        await Call(user, "DELETE", "/pots/{potId}", route);

        api.Client.AssertCovers();
    }

    [Fact]
    public async Task The_free_tier_stops_at_its_pot_limit_and_says_which_tier_is_next()
    {
        var user = GreenhausFixture.NewUser();
        await NewPot(user, "One");
        await NewPot(user, "Two");

        var problem = await Json(Call(user, "POST", "/pots", body: new { name = "Three", kind = "pot" }), HttpStatusCode.Forbidden);
        Assert.Equal("limit_reached", problem["code"]!.GetValue<string>());
        Assert.Equal("pots", problem["limitType"]!.GetValue<string>());
        Assert.Equal("gardener", problem["requiredTier"]!.GetValue<string>());

        // The refusal is recorded for the rest of the system, which can offer an upgrade.
        var queued = (await api.WithDb(db => db.Outbox.Where(m => m.Type == "limit.reached.v1").ToListAsync())).Count(m => m.Payload.Contains(user));
        Assert.Equal(1, queued);
    }

    [Fact]
    public async Task A_higher_tier_lifts_the_limit()
    {
        var user = GreenhausFixture.NewUser();
        await NewPot(user, "One");
        await NewPot(user, "Two");
        await api.Grant(user, Tier.Gardener);
        await NewPot(user, "Three");
        Assert.Equal(3, (await Json(Call(user, "GET", "/pots"), HttpStatusCode.OK)).AsArray().Count);
    }

    [Fact]
    public async Task Another_users_pot_does_not_exist()
    {
        var owner = GreenhausFixture.NewUser();
        var stranger = GreenhausFixture.NewUser();
        var potId = await NewPot(owner);

        await Json(Call(stranger, "GET", "/pots/{potId}", new { potId }), HttpStatusCode.NotFound);
        await Json(Call(stranger, "DELETE", "/pots/{potId}", new { potId }), HttpStatusCode.NotFound);
        Assert.Empty((await Json(Call(stranger, "GET", "/pots"), HttpStatusCode.OK)).AsArray());
    }

    [Fact]
    public async Task The_zone_grid_needs_a_paid_tier_and_is_capped_by_it()
    {
        var user = GreenhausFixture.NewUser();
        var potId = await NewPot(user);
        var route = new { potId };

        var denied = await Json(Call(user, "PUT", "/pots/{potId}/view-mode", route, new { mode = "zones", gridRows = 2, gridCols = 2 }), HttpStatusCode.Forbidden);
        Assert.Equal("tier_required", denied["code"]!.GetValue<string>());

        await api.Grant(user, Tier.Gardener);
        var tooBig = await Json(Call(user, "PUT", "/pots/{potId}/view-mode", route, new { mode = "zones", gridRows = 3, gridCols = 3 }), HttpStatusCode.Forbidden);
        Assert.Equal("limit_reached", tooBig["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Plants_that_disagree_are_reported_as_a_conflict_instead_of_averaged()
    {
        var user = GreenhausFixture.NewUser();
        var potId = await NewPot(user);
        var catalog = await Json(Call(user, "GET", "/species"), HttpStatusCode.OK);
        // Find two species whose ranges for one metric do not overlap.
        JsonNode? cold = null, warm = null;
        string metric = "";
        foreach (var m in new[] { "temperature", "air_humidity", "soil_moisture", "light" })
            foreach (var a in catalog.AsArray())
                foreach (var b in catalog.AsArray())
                    if (cold is null && a!["preferred"]![m] is { } ta && b!["preferred"]![m] is { } tb && ta["max"]!.GetValue<double>() < tb["min"]!.GetValue<double>()) { cold = a; warm = b; metric = m; }
        Assert.NotNull(cold);
        foreach (var species in new[] { cold!, warm! })
            await Json(Call(user, "POST", "/pots/{potId}/plants", new { potId }, new { speciesId = species["id"]!.GetValue<string>(), nickname = species["commonName"]!.GetValue<string>() }), HttpStatusCode.Created);

        var suggestion = await Json(Call(user, "GET", "/pots/{potId}/requirements/suggestion", new { potId }), HttpStatusCode.OK);
        var conflict = Assert.Single(suggestion["conflicts"]!.AsArray(), c => c!["metric"]!.GetValue<string>() == metric);
        Assert.Equal(2, conflict!["plants"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_fertilizing_schedule_is_added_once_per_species_and_repeats_when_done()
    {
        var user = GreenhausFixture.NewUser();
        var potId = await NewPot(user);
        var route = new { potId };
        await Json(Call(user, "POST", "/pots/{potId}/plants", route, new { speciesId = "basil", nickname = "A" }), HttpStatusCode.Created);
        await Json(Call(user, "POST", "/pots/{potId}/plants", route, new { speciesId = "basil", nickname = "B" }), HttpStatusCode.Created);

        var open = await Json(Call(user, "GET", "/pots/{potId}/care-tasks", route, query: "state=open"), HttpStatusCode.OK);
        var task = Assert.Single(open.AsArray());
        await Json(Call(user, "POST", "/pots/{potId}/care-tasks/{taskId}/complete", new { potId, taskId = task!["id"]!.GetValue<string>() }), HttpStatusCode.OK);

        var next = await Json(Call(user, "GET", "/pots/{potId}/care-tasks", route, query: "state=open"), HttpStatusCode.OK);
        Assert.Single(next.AsArray());
    }

    [Fact]
    public async Task Invalid_input_is_a_documented_400()
    {
        var user = GreenhausFixture.NewUser();
        var problem = await Json(Call(user, "POST", "/pots", body: new { name = "", kind = "pot" }), HttpStatusCode.BadRequest);
        Assert.Equal("validation_failed", problem["code"]!.GetValue<string>());
    }
}