using Greenhaus.Api.Auth;
using Greenhaus.Api.Contracts;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

public static class PotEndpoints
{
    public static void MapPots(this RouteGroupBuilder api)
    {
        api.MapGet("/pots", async (HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Pots.AsNoTracking().Where(p => p.OwnerId == http.User.UserId()).OrderBy(p => p.CreatedAt).ToListAsync(ct)).Select(p => p.ToDto()).ToList()));

        api.MapPost("/pots", async (PotCreate body, HttpContext http, GreenhausDbContext db, EntitlementService entitlements, EventPublisher events, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 60 || body.Kind is null)
                return Problems.Invalid("A name (1 to 60 characters) and a kind are required");

            var user = http.User.UserId();
            var tier = await entitlements.TierOfAsync(user, ct);
            var limit = TierPolicy.LimitsFor(tier).Pots;
            if (limit is { } max && await db.Pots.CountAsync(p => p.OwnerId == user, ct) >= max)
            {
                await events.LimitReachedAsync(user, "pots", tier, ct);
                return Problems.Fail(403, "limit_reached", "Pot limit reached", $"The {tier} tier allows {max} pots", "pots", TierPolicy.Next(tier));
            }

            var pot = new PotEntity
            {
                Id = Guid.NewGuid(), OwnerId = user, Name = body.Name.Trim(), Location = body.Location, Kind = body.Kind.Value,
                ViewMode = ViewMode.Simple, CreatedAt = clock.GetUtcNow(),
            };
            pot.Zones.AddRange(Mapping.BuildZones(pot.Id, 1, 1, simple: true));
            // A new pot starts with nothing measured. Adding plants offers a suggestion to take over.
            pot.Requirements.AddRange(Enum.GetValues<Metric>().Select(m => new RequirementEntity { PotId = pot.Id, Metric = m, Enabled = false }));
            db.Pots.Add(pot);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/greenhaus/v1/pots/{pot.Id}", pot.ToDto());
        });

        api.MapGet("/pots/{potId:guid}", async (Guid potId, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
            await db.OwnedPotAsync(potId, http.User.UserId(), ct) is { } pot ? Results.Ok(pot.ToDto()) : Problems.NotFound());

        api.MapPatch("/pots/{potId:guid}", async (Guid potId, PotUpdate body, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is not { } pot) return Problems.NotFound();
            if (body.Name is null && body.Location is null) return Problems.Invalid("Nothing to update");
            if (body.Name is { Length: 0 or > 60 }) return Problems.Invalid("Name must be 1 to 60 characters");
            if (body.Name is not null) pot.Name = body.Name.Trim();
            if (body.Location is not null) pot.Location = body.Location;
            await db.SaveChangesAsync(ct);
            return Results.Ok(pot.ToDto());
        });

        api.MapDelete("/pots/{potId:guid}", async (Guid potId, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is not { } pot) return Problems.NotFound();
            db.Pots.Remove(pot);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapPut("/pots/{potId:guid}/view-mode", SetViewMode);
        api.MapGet("/pots/{potId:guid}/zones", async (Guid potId, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var zones = await db.Zones.AsNoTracking().Where(z => z.PotId == potId).OrderBy(z => z.Row).ThenBy(z => z.Col).ToListAsync(ct);
            return Results.Ok(zones.Select(z => z.ToDto()).ToList());
        });

        api.MapPatch("/pots/{potId:guid}/zones/{zoneId:guid}", async (Guid potId, Guid zoneId, LabelUpdate body, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == zoneId && z.PotId == potId, ct);
            if (zone is null) return Problems.NotFound("Unknown zone");
            if (string.IsNullOrWhiteSpace(body.Label) || body.Label.Length > 40) return Problems.Invalid("Label must be 1 to 40 characters");
            zone.Label = body.Label.Trim();
            await db.SaveChangesAsync(ct);
            return Results.Ok(zone.ToDto());
        });

        api.MapGet("/pots/{potId:guid}/plants", async (Guid potId, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var plants = await db.Plants.AsNoTracking().Where(p => p.PotId == potId).OrderBy(p => p.PlantedAt).ThenBy(p => p.Nickname).ToListAsync(ct);
            return Results.Ok(plants.Select(p => p.ToDto()).ToList());
        });

        api.MapPost("/pots/{potId:guid}/plants", async (Guid potId, PlantCreate body, HttpContext http, GreenhausDbContext db, SpeciesCatalog catalog, TimeProvider clock, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            if (string.IsNullOrWhiteSpace(body.SpeciesId) || string.IsNullOrWhiteSpace(body.Nickname) || body.Nickname.Length > 60)
                return Problems.Invalid("A species and a nickname (1 to 60 characters) are required");
            if (body.Notes is { Length: > 500 }) return Problems.Invalid("Notes may have at most 500 characters");
            if (catalog.Find(body.SpeciesId) is not { } species) return Problems.Fail(400, "unknown_species", "Unknown species");
            if (body.ZoneId is { } zoneId && !await db.Zones.AnyAsync(z => z.Id == zoneId && z.PotId == potId, ct))
                return Problems.Invalid("The zone does not belong to this pot");

            var plant = new PlantEntity
            {
                Id = Guid.NewGuid(), PotId = potId, SpeciesId = species.Id, Nickname = body.Nickname.Trim(), ZoneId = body.ZoneId,
                PlantedAt = body.PlantedAt ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), Notes = body.Notes,
            };
            db.Plants.Add(plant);
            // A species with a fertilizing schedule gets a reminder, once, so adding a second basil does not nag twice.
            if (species.FertilizerIntervalDays is { } days && !await db.Tasks.AnyAsync(t => t.PotId == potId && t.Kind == "fertilize" && t.Origin == "species_schedule" && t.State == "open" && t.Title == $"Fertilize: {species.CommonName}", ct))
            {
                db.Tasks.Add(new CareTaskEntity
                {
                    Id = Guid.NewGuid(), PotId = potId, Kind = "fertilize", Title = $"Fertilize: {species.CommonName}", DueAt = clock.GetUtcNow().AddDays(days),
                    State = "open", Origin = "species_schedule", RepeatEveryDays = days,
                });
            }
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/greenhaus/v1/pots/{potId}/plants/{plant.Id}", plant.ToDto());
        });

        api.MapPatch("/pots/{potId:guid}/plants/{plantId:guid}", async (Guid potId, Guid plantId, PlantUpdate body, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var plant = await db.Plants.FirstOrDefaultAsync(p => p.Id == plantId && p.PotId == potId, ct);
            if (plant is null) return Problems.NotFound("Unknown plant");
            if (body.Nickname is null && body.ZoneId is null && body.Notes is null) return Problems.Invalid("Nothing to update");
            if (body.Nickname is { Length: 0 or > 60 }) return Problems.Invalid("Nickname must be 1 to 60 characters");
            if (body.ZoneId is { } zoneId && !await db.Zones.AnyAsync(z => z.Id == zoneId && z.PotId == potId, ct))
                return Problems.Invalid("The zone does not belong to this pot");
            if (body.Nickname is not null) plant.Nickname = body.Nickname.Trim();
            if (body.ZoneId is not null) plant.ZoneId = body.ZoneId;
            if (body.Notes is not null) plant.Notes = body.Notes;
            await db.SaveChangesAsync(ct);
            return Results.Ok(plant.ToDto());
        });

        api.MapDelete("/pots/{potId:guid}/plants/{plantId:guid}", async (Guid potId, Guid plantId, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var removed = await db.Plants.Where(p => p.Id == plantId && p.PotId == potId).ExecuteDeleteAsync(ct);
            return removed > 0 ? Results.NoContent() : Problems.NotFound("Unknown plant");
        });
    }

    private static async Task<IResult> SetViewMode(Guid potId, ViewModeUpdate body, HttpContext http, GreenhausDbContext db, EntitlementService entitlements, EventPublisher events, CancellationToken ct)
    {
        var user = http.User.UserId();
        var pot = await db.Pots.Include(p => p.Zones).Include(p => p.Channels).Include(p => p.Plants)
            .FirstOrDefaultAsync(p => p.Id == potId && p.OwnerId == user, ct);
        if (pot is null) return Problems.NotFound();
        if (body.Mode is null) return Problems.Invalid("A mode is required");

        var tier = await entitlements.TierOfAsync(user, ct);
        var limits = TierPolicy.LimitsFor(tier);

        if (body.Mode == ViewMode.Zones)
        {
            var rows = body.GridRows ?? 2;
            var cols = body.GridCols ?? 2;
            if (rows < 1 || cols < 1 || rows > 8 || cols > 8) return Problems.Invalid("A grid has 1 to 8 rows and columns");
            if (limits.ZoneGridMax < 2)
            {
                await events.LimitReachedAsync(user, "zone_grid", tier, ct);
                return Problems.Fail(403, "tier_required", "The zone grid needs a higher tier", null, "zone_grid", Tier.Gardener);
            }
            if (rows > limits.ZoneGridMax || cols > limits.ZoneGridMax)
            {
                await events.LimitReachedAsync(user, "zone_grid", tier, ct);
                return Problems.Fail(403, "limit_reached", "Grid too large for this tier", $"The {tier} tier allows up to {limits.ZoneGridMax}x{limits.ZoneGridMax}", "zone_grid", TierPolicy.Next(tier));
            }

            var zones = Mapping.BuildZones(potId, rows, cols, simple: false);
            db.Zones.RemoveRange(pot.Zones);
            db.Zones.AddRange(zones);
            // Channels of one metric are spread over the zones in order, and plants are placed again by the user.
            foreach (var group in pot.Channels.Where(c => c.Active).GroupBy(c => c.Metric))
            {
                var index = 0;
                foreach (var channel in group) channel.ZoneId = zones[index++ % zones.Count].Id;
            }
            foreach (var plant in pot.Plants) plant.ZoneId = null;
            pot.ViewMode = ViewMode.Zones;
            pot.GridRows = rows;
            pot.GridCols = cols;
            await db.SaveChangesAsync(ct);
            return Results.Ok(pot.ToDto());
        }

        // Back to the Simple view: one channel per metric stays, chosen by the user when there are several.
        var active = pot.Channels.Where(c => c.Active).ToList();
        foreach (var group in active.GroupBy(c => c.Metric).Where(g => g.Count() > 1))
        {
            var chosen = body.PrimaryChannels?.FirstOrDefault(pc => pc.Metric == group.Key && group.Any(c => c.ChannelId == pc.ChannelId));
            if (chosen is null) return Problems.Fail(400, "primary_channel_required", "Choose a primary channel", $"More than one {MetricNames.Key(group.Key)} channel exists");
            // The others keep their history but stop being shown.
            foreach (var channel in group.Where(c => c.ChannelId != chosen.ChannelId)) channel.Active = false;
        }

        var single = Mapping.BuildZones(potId, 1, 1, simple: true);
        db.Zones.RemoveRange(pot.Zones);
        db.Zones.AddRange(single);
        foreach (var channel in pot.Channels.Where(c => c.Active)) channel.ZoneId = single[0].Id;
        foreach (var plant in pot.Plants) plant.ZoneId = null;
        pot.ViewMode = ViewMode.Simple;
        pot.GridRows = null;
        pot.GridCols = null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(pot.ToDto());
    }
}