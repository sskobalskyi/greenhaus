using System.Net;
using System.Text.Json.Nodes;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Tests;

[Collection("api")]
public class AssistantTests(GreenhausFixture api)
{
    private Task<HttpResponseMessage> Ask(string user, string potId, string question) =>
        api.Client.Call("POST", "/pots/{potId}/assistant/ask", new { potId }, new { question }, null, GreenhausFixture.As(user));

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return await api.Client.Node(r);
    }

    private async Task<string> NewPot(string user, string name = "Herbs")
    {
        var pot = await Json(api.Client.Call("POST", "/pots", null, new { name, kind = "pot" }, null, GreenhausFixture.As(user)), HttpStatusCode.Created);
        return pot["id"]!.GetValue<string>();
    }

    private async Task<int> Used(string user) =>
        (await Json(api.Client.Call("GET", "/me/entitlements", null, null, null, GreenhausFixture.As(user)), HttpStatusCode.OK))["usage"]!["aiQuestionsThisMonth"]!.GetValue<int>();

    /// <summary>The events of a stream, in order.</summary>
    private static async Task<List<JsonNode>> Events(HttpResponseMessage response) =>
        [.. (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => JsonNode.Parse(e["data:".Length..].Trim())!)];

    private static string Unique(string prefix = "") => prefix + "Is it ok? " + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task An_answer_streams_through_unchanged_and_counts_once()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        var question = Unique();

        var response = await Ask(user, pot, question);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var events = await Events(response);
        Assert.Equal(["token", "token", "done"], events.Select(e => e["type"]!.GetValue<string>()));
        Assert.Equal("Looks fine.", string.Concat(events.Where(e => e["type"]!.GetValue<string>() == "token").Select(e => e["text"]!.GetValue<string>())));
        Assert.Equal("answer", events[^1]["mode"]!.GetValue<string>());

        Assert.Equal(1, await Used(user));
        // Docs-Brain is asked for this person, with the facts about the pot.
        var call = api.Docs.CallFor(question)!;
        Assert.Equal(user, call.Tenant);
        Assert.Contains("Pot name: \"Herbs\"", call.Context);
    }

    [Fact]
    public async Task Fallbacks_and_broken_answers_do_not_count_against_the_quota()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);

        var retrieval = await Ask(user, pot, Unique("RETRIEVAL:"));
        Assert.Equal("retrieval_only", (await Events(retrieval))[^1]["mode"]!.GetValue<string>());
        var none = await Ask(user, pot, Unique("NORESULTS:"));
        Assert.Equal("no_results", (await Events(none))[^1]["mode"]!.GetValue<string>());

        // Broken off mid-answer, and ended without done: the stream is closed properly and says why.
        foreach (var prefix in new[] { "CUTOFF:", "NODONE:" })
        {
            var events = await Events(await Ask(user, pot, Unique(prefix)));
            Assert.Equal("done", events[^1]["type"]!.GetValue<string>());
            Assert.Equal("retrieval_only", events[^1]["mode"]!.GetValue<string>());
            Assert.Equal("assistant_unavailable", events[^1]["reason"]!.GetValue<string>());
        }

        Assert.Equal(0, await Used(user));
    }

    [Fact]
    public async Task An_unavailable_assistant_is_a_503_and_costs_nothing()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);

        var problem = await Json(Ask(user, pot, Unique("DOWN:")), HttpStatusCode.ServiceUnavailable);
        Assert.Equal("assistant_unavailable", problem["code"]!.GetValue<string>());
        Assert.Equal(0, await Used(user));
    }

    [Fact]
    public async Task The_monthly_quota_of_the_tier_stops_further_questions_and_is_reported_to_the_funnel()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await Ask(user, pot, Unique())).StatusCode);
        Assert.Equal(10, await Used(user));

        var refused = Unique();
        var problem = await Json(Ask(user, pot, refused), HttpStatusCode.Forbidden);
        Assert.Equal("limit_reached", problem["code"]!.GetValue<string>());
        Assert.Equal("ai_questions", problem["limitType"]!.GetValue<string>());
        Assert.Equal("gardener", problem["requiredTier"]!.GetValue<string>());
        Assert.Null(api.Docs.CallFor(refused));

        var queued = (await api.WithDb(db => db.Outbox.Where(m => m.Type == "limit.reached.v1").ToListAsync())).Count(m => m.Payload.Contains(user) && m.Payload.Contains("ai_questions"));
        Assert.Equal(1, queued);

        // A higher tier has a bigger quota.
        await api.Grant(user, Tier.Gardener);
        Assert.Equal(HttpStatusCode.OK, (await Ask(user, pot, Unique())).StatusCode);
    }

    [Fact]
    public async Task The_quota_is_per_calendar_month()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        var lastMonth = Greenhaus.Api.Infrastructure.AssistantUsage.MonthOf(DateTimeOffset.UtcNow.AddMonths(-1));
        await api.WithDb(async db =>
        {
            db.AssistantUsage.Add(new AssistantUsageEntity { UserId = user, Month = lastMonth, Count = 10 });
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(0, await Used(user));
        Assert.Equal(HttpStatusCode.OK, (await Ask(user, pot, Unique())).StatusCode);
        Assert.Equal(1, await Used(user));
    }

    [Fact]
    public async Task Bad_questions_and_other_peoples_pots_are_refused()
    {
        var owner = GreenhausFixture.NewUser();
        var pot = await NewPot(owner);

        Assert.Equal("validation_failed", (await Json(Ask(owner, pot, ""), HttpStatusCode.BadRequest))["code"]!.GetValue<string>());
        await Json(Ask(owner, pot, new string('x', 501)), HttpStatusCode.BadRequest);
        await Json(Ask(GreenhausFixture.NewUser(), pot, Unique()), HttpStatusCode.NotFound);
        await Json(Ask(owner, Guid.NewGuid().ToString(), Unique()), HttpStatusCode.NotFound);
    }

    /// <summary>A user with a pot, a basil, a claimed kit and a reading, on the given tier.</summary>
    private async Task<(string User, string Pot, string Device)> PotWithKit(Tier tier)
    {
        var user = GreenhausFixture.NewUser();
        await api.Grant(user, tier);
        var pot = await NewPot(user, "Kitchen herbs");
        var device = "ast-" + Guid.NewGuid().ToString("N")[..8];
        api.Pulse.Add(FakePulse.Kit(device), "t");
        await Json(api.Client.Call("POST", "/pots/{potId}/plants", new { potId = pot }, new { speciesId = "basil", nickname = "Genovese" }, null, GreenhausFixture.As(user)), HttpStatusCode.Created);
        var requirements = await Json(api.Client.Call("GET", "/pots/{potId}/requirements", new { potId = pot }, null, null, GreenhausFixture.As(user)), HttpStatusCode.OK);
        var soil = requirements["metrics"]!.AsArray().Single(m => m!["metric"]!.GetValue<string>() == "soil_moisture")!;
        soil["enabled"] = true; soil["min"] = 40; soil["max"] = 70;
        await Json(api.Client.Call("PUT", "/pots/{potId}/requirements", new { potId = pot }, requirements, null, GreenhausFixture.As(user)), HttpStatusCode.OK);
        await Json(api.Client.Call("POST", "/claims", null, new { deviceId = device, token = "t", potId = pot }, null, GreenhausFixture.As(user)), HttpStatusCode.Created);
        api.Pulse.Reading(device, "soil", 25, DateTimeOffset.UtcNow);
        api.Pulse.Series(device, "soil", new Greenhaus.Api.Infrastructure.PulsePoint(DateTimeOffset.UtcNow.AddDays(-2), 30, 20, 40), new Greenhaus.Api.Infrastructure.PulsePoint(DateTimeOffset.UtcNow.AddDays(-1), 50, 45, 61));
        return (user, pot, device);
    }

    [Fact]
    public async Task What_the_assistant_is_told_depends_on_the_tier()
    {
        var contexts = new Dictionary<Tier, string>();
        foreach (var tier in new[] { Tier.Novice, Tier.Gardener, Tier.Druid })
        {
            var (user, pot, _) = await PotWithKit(tier);
            var question = Unique();
            Assert.Equal(HttpStatusCode.OK, (await Ask(user, pot, question)).StatusCode);
            contexts[tier] = api.Docs.CallFor(question)!.Context;
        }

        // Everyone: the pot, the plants with what the species prefers, the targets. The text a person typed is delimited.
        foreach (var context in contexts.Values)
        {
            Assert.Contains("Plant 1: Basil.", context);
            Assert.Contains("Target for soil_moisture: 40 to 70.", context);
            Assert.Contains("Plant 1 name: \"Genovese\"", context[context.IndexOf("[user-entered text begins]", StringComparison.Ordinal)..]);
            Assert.True(context.Length <= 4000);
        }

        Assert.DoesNotContain("Reading in zone", contexts[Tier.Novice]);
        Assert.Contains("Reading in zone the pot: soil_moisture 25 %, attention below.", contexts[Tier.Gardener]);
        Assert.Contains("Open alert in zone the pot", contexts[Tier.Gardener]);
        Assert.DoesNotContain("Last 7 days", contexts[Tier.Gardener]);
        Assert.Contains("Reading in zone the pot: soil_moisture 25 %", contexts[Tier.Druid]);
        Assert.Contains("Last 7 days in zone the pot: soil_moisture min 20, average 40, max 61 %.", contexts[Tier.Druid]);
        Assert.Contains("Automation log", contexts[Tier.Druid]);
    }

    [Fact]
    public async Task A_pulse_that_is_down_leaves_a_note_instead_of_failing_the_question()
    {
        var (user, pot, _) = await PotWithKit(Tier.Druid);
        var question = Unique();
        api.Pulse.Down = true;
        try { Assert.Equal(HttpStatusCode.OK, (await Ask(user, pot, question)).StatusCode); }
        finally { api.Pulse.Down = false; }

        var context = api.Docs.CallFor(question)!.Context;
        Assert.Contains("The history of the readings could not be loaded", context);
        Assert.Contains("No recent readings have arrived", context);
    }
}