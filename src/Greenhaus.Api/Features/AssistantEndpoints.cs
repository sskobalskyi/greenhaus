using System.Text;
using System.Text.Json;
using Greenhaus.Api.Auth;
using Greenhaus.Api.Contracts;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Messaging;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

public sealed class AssistantQuestionDto
{
    public string? Question { get; set; }
}

public static class AssistantEndpoints
{
    public const string RateLimitPolicy = "assistant";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static void MapAssistant(this RouteGroupBuilder api) =>
        api.MapPost("/pots/{potId:guid}/assistant/ask", Ask).RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// Asks the assistant about a pot and streams the answer. The tier decides how many questions a month and how much of the
    /// pot the assistant is told. A question counts when the assistant really answered, which is when the stream ends with a
    /// `done` of mode `answer`: a fallback with passages only, or no result, costs nothing.
    /// </summary>
    private static async Task Ask(Guid potId, AssistantQuestionDto body, HttpContext http, GreenhausDbContext db, EntitlementService entitlements,
        EventPublisher events, AssistantContextService contexts, IAssistantClient assistant, TimeProvider clock, ILogger<AssistantContextService> log, CancellationToken ct)
    {
        var user = http.User.UserId();
        if (await db.OwnedPotAsync(potId, user, ct) is not { } pot) { await Problems.NotFound().ExecuteAsync(http); return; }
        var question = body.Question?.Trim();
        if (string.IsNullOrEmpty(question) || question.Length > 500)
        {
            await Problems.Invalid("A question of 1 to 500 characters is required").ExecuteAsync(http);
            return;
        }

        var tier = await entitlements.TierOfAsync(user, ct);
        var limits = TierPolicy.LimitsFor(tier);
        // A place in the quota is taken before anything is sent, so questions that start together cannot all pass a check that none of them has counted in yet.
        var reservation = await AssistantUsage.TryReserveAsync(db, user, clock.GetUtcNow(), limits.AiQuestionsPerMonth, ct);
        if (reservation is null)
        {
            await events.LimitReachedAsync(user, "ai_questions", tier, ct);
            await Problems.Fail(403, "limit_reached", "Question limit reached", $"The {tier} tier allows {limits.AiQuestionsPerMonth} questions a month", "ai_questions", TierPolicy.Next(tier)).ExecuteAsync(http);
            return;
        }

        // The place is held for as long as the question runs. It becomes a counted question when the assistant really answered, and is
        // given back in every other case, including a person who went away, so the finally block does not listen to cancellation.
        var answered = false;
        try { answered = await Stream(pot, user, question, limits, http, contexts, assistant, log, ct); }
        finally
        {
            if (answered) await AssistantUsage.ConfirmAsync(db, reservation, CancellationToken.None);
            else await AssistantUsage.ReleaseAsync(db, reservation, CancellationToken.None);
        }
    }

    /// <summary>Streams the answer through. True when it ended with a `done` of mode `answer`.</summary>
    private static async Task<bool> Stream(PotEntity pot, string user, string question, TierLimits limits, HttpContext http, AssistantContextService contexts,
        IAssistantClient assistant, ILogger log, CancellationToken ct)
    {
        var context = await contexts.BuildAsync(pot, limits, ct);
        await using var stream = assistant.AskAsync(user, question, context, ct).GetAsyncEnumerator(ct);

        // Nothing is sent before the first event arrives, so a refusal or an unreachable assistant is still a proper 503.
        string? first;
        try { first = await stream.MoveNextAsync() ? stream.Current : null; }
        catch (AssistantUnavailableException e) { log.LogWarning("The assistant is unavailable: {Reason}", e.Message); first = null; }
        if (first is null)
        {
            GreenhausTelemetry.AssistantQuestions.Add(1, new KeyValuePair<string, object?>("mode", "unavailable"));
            await Problems.Fail(503, "assistant_unavailable", "The assistant is not available right now").ExecuteAsync(http);
            return false;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        string? mode = null;
        async Task Send(string json)
        {
            await http.Response.WriteAsync($"data: {json}\n\n", Encoding.UTF8, ct);
            await http.Response.Body.FlushAsync(ct);
        }

        var line = first;
        try
        {
            while (true)
            {
                mode = ModeOfDone(line) ?? mode;
                await Send(line);
                if (!await stream.MoveNextAsync()) break;
                line = stream.Current;
            }
        }
        catch (AssistantUnavailableException e)
        {
            log.LogWarning("The assistant broke off: {Reason}", e.Message);
            mode = null;
        }

        if (mode is null)
        {
            // The stream broke off, or ended without a done: finish it properly and do not count it.
            await Send(JsonSerializer.Serialize(new { type = "done", mode = "retrieval_only", reason = "assistant_unavailable" }, Json));
            GreenhausTelemetry.AssistantQuestions.Add(1, new KeyValuePair<string, object?>("mode", "unavailable"));
            return false;
        }

        GreenhausTelemetry.AssistantQuestions.Add(1, new KeyValuePair<string, object?>("mode", mode));
        return mode == "answer";
    }

    /// <summary>The mode of a `done` event, or null for any other event.</summary>
    private static string? ModeOfDone(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "done" && doc.RootElement.TryGetProperty("mode", out var mode)
                ? mode.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Gathers the facts about a pot that the tier lets the assistant see, and has the pure builder write them down.</summary>
public sealed class AssistantContextService(GreenhausDbContext db, IReadingSource readings, IPulseClient pulse, SpeciesCatalog catalog, TimeProvider clock, ILogger<AssistantContextService> log)
{
    public async Task<string> BuildAsync(PotEntity pot, TierLimits limits, CancellationToken ct)
    {
        var access = limits.AiDataAccess;
        var notes = new List<string>();

        var plants = await db.Plants.AsNoTracking().Where(p => p.PotId == pot.Id).OrderBy(p => p.PlantedAt).ThenBy(p => p.Nickname).ToListAsync(ct);
        var plantFacts = plants.Select(p =>
        {
            var species = catalog.Find(p.SpeciesId);
            return new PlantFact(p.Nickname, species?.CommonName, p.Notes, species?.Preferred ?? new Dictionary<Metric, Domain.Range>());
        }).ToList();
        var requirements = (await db.Requirements.AsNoTracking().Where(r => r.PotId == pot.Id && r.Enabled).ToListAsync(ct))
            .Select(r => new RequirementFact(r.Metric, r.Min, r.Max)).ToList();

        List<ReadingFact> readingFacts = [];
        List<AlertFact> alertFacts = [];
        List<HistoryFact> history = [];
        List<CareFact> care = [];
        List<AuditFact> audit = [];

        if (access >= AiDataAccess.Current)
        {
            var zones = await db.Zones.AsNoTracking().Where(z => z.PotId == pot.Id).OrderBy(z => z.Row).ThenBy(z => z.Col).ToListAsync(ct);
            var names = zones.ToDictionary(z => z.Id, z => ZoneName(pot, z));
            var status = await StatusBuilder.BuildAsync(db, readings, clock, pot.Id, ct);
            foreach (var zone in status.Zones)
                foreach (var m in zone.Metrics)
                    readingFacts.Add(new ReadingFact(names.GetValueOrDefault(zone.ZoneId, "the pot"), m.Metric, m.Value, m.Unit, m.State, m.Direction));
            alertFacts = [.. (status.Alerts ?? []).Select(a => new AlertFact(names.GetValueOrDefault(a.ZoneId, "the pot"), a.Metric, a.Message ?? ""))];
            if (readingFacts.Count > 0 && readingFacts.All(r => r.Value is null)) notes.Add("No recent readings have arrived, the controller may be offline");

            if (access >= AiDataAccess.History)
            {
                history = await HistoryAsync(pot, zones, names, limits, notes, ct);
                care = [.. (await db.Tasks.AsNoTracking().Where(t => t.PotId == pot.Id && t.State == "done").OrderByDescending(t => t.CompletedAt).Take(5).ToListAsync(ct))
                    .Select(t => new CareFact(t.Title, t.State, t.CompletedAt ?? t.DueAt, UserEntered: t.Origin == "user"))];
                audit = [.. (await db.Audit.AsNoTracking().Where(a => a.PotId == pot.Id).OrderByDescending(a => a.At).Take(8).ToListAsync(ct))
                    .Select(a => new AuditFact(a.Actor, a.Action, a.Reason, a.At))];
            }
        }

        return AssistantContextBuilder.Build(new AssistantFacts(pot.Name, pot.Kind, pot.ViewMode, plantFacts, requirements, readingFacts, alertFacts, history, care, audit, notes), access);
    }

    private async Task<List<HistoryFact>> HistoryAsync(PotEntity pot, List<ZoneEntity> zones, Dictionary<Guid, string> names, TierLimits limits, List<string> notes, CancellationToken ct)
    {
        var days = Math.Min(7, limits.HistoryDays);
        var now = clock.GetUtcNow();
        var channels = await db.Channels.AsNoTracking().Where(c => c.PotId == pot.Id && c.Active).ToListAsync(ct);
        var failed = false;
        var results = await Task.WhenAll(channels.Select(async channel =>
        {
            try
            {
                var points = await pulse.SeriesAsync(channel.DeviceId, channel.ChannelId, now.AddDays(-days), now, "1h", ct);
                if (points.Count == 0) return null;
                return new HistoryFact(names.GetValueOrDefault(channel.ZoneId, "the pot"), channel.Metric, channel.Unit, days,
                    points.Min(p => p.Min ?? p.Value), points.Average(p => p.Value), points.Max(p => p.Max ?? p.Value));
            }
            catch (PulseException e)
            {
                log.LogWarning("No history for {Device}/{Channel}: {Code}", channel.DeviceId, channel.ChannelId, e.Code);
                failed = true;
                return null;
            }
        }));
        // Missing history is told to the assistant so it does not pretend to know it, and never fails the question.
        if (failed) notes.Add("The history of the readings could not be loaded");
        return [.. results.Where(r => r is not null).Select(r => r!)];
    }

    /// <summary>A neutral name for a zone. Labels are typed by the user, so they stay out of the facts.</summary>
    private static string ZoneName(PotEntity pot, ZoneEntity zone) =>
        pot.ViewMode == ViewMode.Simple ? "the pot" : $"row {zone.Row + 1} column {zone.Col + 1}";
}
