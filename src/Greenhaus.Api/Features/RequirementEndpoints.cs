using Greenhaus.Api.Auth;
using Greenhaus.Api.Contracts;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

public static class RequirementEndpoints
{
    public static void MapRequirements(this RouteGroupBuilder api)
    {
        api.MapGet("/pots/{potId:guid}/requirements", async (Guid potId, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            return Results.Ok((await db.Requirements.AsNoTracking().Where(r => r.PotId == potId).ToListAsync(ct)).ToDto());
        });

        api.MapPut("/pots/{potId:guid}/requirements", async (Guid potId, RequirementsDto body, HttpContext http, GreenhausDbContext db, DeviceSync sync, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var drafts = body.Metrics.Select(m => new RequirementDraft(m.Metric, m.Enabled, m.Min, m.Max, m.Margin)).ToList();
            if (!RequirementsSuggester.IsValid(drafts, out var problem)) return Problems.Invalid(problem!);
            if (drafts.Any(d => d.Enabled && d.Min is null && d.Max is null)) return Problems.Invalid("An enabled metric needs a minimum or a maximum");
            if (drafts.Any(d => d.Margin is < 0)) return Problems.Invalid("A margin cannot be negative");

            var existing = await db.Requirements.Where(r => r.PotId == potId).ToListAsync(ct);
            foreach (var draft in drafts)
            {
                var row = existing.FirstOrDefault(r => r.Metric == draft.Metric);
                if (row is null) db.Requirements.Add(row = new RequirementEntity { PotId = potId, Metric = draft.Metric });
                row.Enabled = draft.Enabled; row.Min = draft.Min; row.Max = draft.Max; row.Margin = draft.Margin;
            }
            await db.SaveChangesAsync(ct);
            await sync.SyncRulesAsync(potId, ct);
            return Results.Ok((await db.Requirements.AsNoTracking().Where(r => r.PotId == potId).ToListAsync(ct)).ToDto());
        });

        api.MapGet("/pots/{potId:guid}/requirements/suggestion", async (Guid potId, HttpContext http, GreenhausDbContext db, SpeciesCatalog catalog, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var plants = await db.Plants.AsNoTracking().Where(p => p.PotId == potId).ToListAsync(ct);
            var preferences = plants.Select(p => catalog.Find(p.SpeciesId)).Zip(plants, (s, p) => (s, p)).Where(x => x.s is not null)
                .Select(x => new PlantPreference(x.p.Id, x.s!.Preferred)).ToList();
            var suggestion = RequirementsSuggester.Suggest(preferences);
            return Results.Ok(new RequirementsSuggestionDto
            {
                Requirements = new RequirementsDto { Metrics = [.. suggestion.Requirements.Select(r => new MetricRequirementDto { Metric = r.Metric, Enabled = r.Enabled, Min = r.Min, Max = r.Max, Margin = r.Margin })] },
                Conflicts = [.. suggestion.Conflicts.Select(c => new RequirementConflictDto { Metric = c.Metric, Plants = [.. c.Plants] })],
            });
        });
    }
}