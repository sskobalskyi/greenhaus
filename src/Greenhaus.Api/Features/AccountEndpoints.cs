using Greenhaus.Api.Auth;
using Greenhaus.Api.Contracts;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

public static class AccountEndpoints
{
    public static void MapAccount(this RouteGroupBuilder api)
    {
        api.MapGet("/me", async (HttpContext http, GreenhausDbContext db, EntitlementService entitlements, CancellationToken ct) =>
            Results.Ok(await ProfileAsync(http, db, entitlements, ct)));

        api.MapPatch("/me", async (ProfileUpdate body, HttpContext http, GreenhausDbContext db, EntitlementService entitlements, CancellationToken ct) =>
        {
            if (body.DisplayName is null && body.Locale is null) return Problems.Invalid("Nothing to update");
            if (body.Locale is not null and not ("en" or "uk")) return Problems.Invalid("Unknown locale");
            if (body.DisplayName is { Length: 0 or > 60 }) return Problems.Invalid("Display name must be 1 to 60 characters");

            var profile = await EnsureProfileAsync(http, db, ct);
            if (body.DisplayName is not null) profile.DisplayName = body.DisplayName;
            if (body.Locale is not null) profile.Locale = body.Locale;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ProfileAsync(http, db, entitlements, ct));
        });

        api.MapGet("/me/entitlements", async (HttpContext http, GreenhausDbContext db, EntitlementService entitlements, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            var pots = await db.Pots.CountAsync(p => p.OwnerId == user, ct);
            return Results.Ok(await entitlements.GetAsync(user, pots, ct));
        });

        // Public: the limits of every tier are visible before sign-up, and the plans page compares them.
        api.MapGet("/tiers", () =>
            Results.Ok(TierPolicy.Tiers.Select(t => new TierLimitsDto { Tier = t, Limits = LimitsDto.From(TierPolicy.LimitsFor(t)) }).ToList()))
            .AllowAnonymous();

        api.MapGet("/species", (string? query, SpeciesCatalog catalog) =>
            Results.Ok(catalog.Search(query).Select(SpeciesDto.From).ToList()));

        api.MapGet("/species/{speciesId}", (string speciesId, SpeciesCatalog catalog) =>
            catalog.Find(speciesId) is { } species ? Results.Ok(SpeciesDto.From(species)) : Problems.NotFound("Unknown species"));
    }

    private static async Task<UserProfileEntity> EnsureProfileAsync(HttpContext http, GreenhausDbContext db, CancellationToken ct)
    {
        var id = http.User.UserId();
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is not null) return profile;

        // The first call creates the profile from what the token says about the person.
        profile = new UserProfileEntity
        {
            Id = id,
            DisplayName = Truncate(http.User.FindFirst("name")?.Value ?? http.User.FindFirst("preferred_username")?.Value ?? id, 60),
            Email = http.User.FindFirst("email")?.Value ?? $"{id}@unknown.invalid",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    private static async Task<ProfileDto> ProfileAsync(HttpContext http, GreenhausDbContext db, EntitlementService entitlements, CancellationToken ct)
    {
        var profile = await EnsureProfileAsync(http, db, ct);
        return new ProfileDto
        {
            Id = profile.Id, DisplayName = profile.DisplayName, Email = profile.Email, Locale = profile.Locale,
            Tier = await entitlements.TierOfAsync(profile.Id, ct), CreatedAt = profile.CreatedAt,
        };
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
