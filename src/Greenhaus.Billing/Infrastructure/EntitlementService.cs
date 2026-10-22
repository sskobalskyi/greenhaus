using Greenhaus.Billing.Messaging;
using Greenhaus.Billing.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Billing.Infrastructure;

/// <summary>
/// Works out the effective entitlement of a user from their subscription and grants, and announces it with
/// entitlement.changed.v1 when it differs from what was announced last. The caller saves, so the event and the change
/// that caused it commit together.
/// </summary>
public sealed class EntitlementService(BillingDbContext db, EventPublisher events, TimeProvider clock)
{
    public async Task RecomputeAsync(string userId, string cause, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sources = new List<EntitlementSource>();

        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (sub is { Status: "active" or "past_due" })
            sources.Add(new EntitlementSource("subscription", sub.Tier, sub.CreatedAt, sub.CurrentPeriodEnd, sub.Status));

        foreach (var g in await db.Grants.AsNoTracking().Where(g => g.UserId == userId && g.Status != "revoked").ToListAsync(ct))
        {
            // A grant at risk counts only until its grace period ends.
            var until = g.Status == "at_risk" && g.GraceEndsAt is { } grace ? (grace < g.ValidUntil ? grace : g.ValidUntil) : g.ValidUntil;
            sources.Add(new EntitlementSource("grant", g.Tier, g.ValidFrom, until, g.Status == "at_risk" ? SourceStatus.AtRisk : SourceStatus.Active));
        }

        var effective = Entitlements.Compute(sources, now);
        var state = await db.EntitlementStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var previous = state?.Tier ?? Tier.Novice;
        if (state is not null && state.Tier == effective.Tier && state.Until == effective.Until && state.Status == effective.Status) return;
        if (state is null && effective.Tier == Tier.Novice) return;

        if (state is null) db.EntitlementStates.Add(state = new EntitlementStateEntity { UserId = userId });
        state.Tier = effective.Tier;
        state.Until = effective.Until;
        state.Status = effective.Status;
        state.UpdatedAt = now;

        events.Stage("entitlement.changed.v1", new
        {
            userId, tier = EventPublisher.Name(effective.Tier), previousTier = EventPublisher.Name(previous),
            reason = ReasonFor(effective, cause), validUntil = effective.Until, at = now,
        });
        BillingTelemetry.EntitlementChanges.Add(1, new KeyValuePair<string, object?>("tier", EventPublisher.Name(effective.Tier)));
    }

    /// <summary>
    /// The reason says where the tier comes from, because the Greenhaus API reads it to label the source. While there is a tier it
    /// follows the source that provides it, so a grant that is not the best source never mislabels a subscription. When the tier
    /// falls to the free one, the cause is the reason.
    /// </summary>
    private static string ReasonFor(EffectiveEntitlement effective, string cause) => (effective.Kind, effective.Status) switch
    {
        (null, _) => cause,
        ("grant", SourceStatus.AtRisk) => "grant_at_risk",
        ("grant", _) => "grant_started",
        ("subscription", SourceStatus.PastDue) => "payment_failed",
        _ => "subscribed",
    };
}