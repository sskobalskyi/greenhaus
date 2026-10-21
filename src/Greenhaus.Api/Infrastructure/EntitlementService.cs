using Greenhaus.Api.Contracts;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Greenhaus.Api.Infrastructure;

public sealed class EntitlementOptions
{
    /// <summary>The tier of a user billing has never mentioned.</summary>
    public Tier DefaultTier { get; set; } = Tier.Novice;
}

/// <summary>
/// The tier a user is on. Billing is the authority and tells us through entitlement events, which are stored here.
/// Until billing has said anything about a user they are on the configured default, which is the free tier.
/// </summary>
public sealed class EntitlementService(GreenhausDbContext db, IOptions<EntitlementOptions> options, TimeProvider clock)
{
    public async Task<Tier> TierOfAsync(string userId, CancellationToken ct)
    {
        var row = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.UserId == userId, ct);
        return row is not null && (row.ValidUntil is null || row.ValidUntil > clock.GetUtcNow()) ? row.Tier : options.Value.DefaultTier;
    }

    public async Task<EntitlementsDto> GetAsync(string userId, int potCount, CancellationToken ct)
    {
        var tier = await TierOfAsync(userId, ct);
        var row = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.UserId == userId, ct);
        var sources = new List<EntitlementSourceDto>();
        if (row is not null && (row.ValidUntil is null || row.ValidUntil > clock.GetUtcNow()))
        {
            sources.Add(new EntitlementSourceDto
            {
                Type = row.Source, Tier = row.Tier, Status = row.Status, OrderId = row.OrderId, ValidUntil = row.ValidUntil,
                GraceEndsAt = row.Status == "at_risk" ? row.ValidUntil : null,
            });
        }

        return new EntitlementsDto
        {
            Tier = tier,
            Limits = LimitsDto.From(TierPolicy.LimitsFor(tier)),
            Usage = new UsageDto { Pots = potCount, AiQuestionsThisMonth = 0 },
            Sources = sources,
        };
    }

    /// <summary>Stores what billing announced. An event older than the one already stored is ignored, because delivery order is not guaranteed.</summary>
    public async Task<bool> SetAsync(string userId, Tier tier, string source, string status, DateTimeOffset? validUntil, DateTimeOffset at, CancellationToken ct)
    {
        var row = await db.Entitlements.FirstOrDefaultAsync(e => e.UserId == userId, ct);
        if (row is not null && row.UpdatedAt > at) return false;
        if (row is null) db.Entitlements.Add(row = new EntitlementEntity { UserId = userId, Source = source, Status = status });
        row.Tier = tier;
        row.Source = source;
        row.Status = status;
        row.ValidUntil = validUntil;
        row.UpdatedAt = at;
        return true;
    }
}