using Greenhaus.Api.Features;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Automation;

/// <param name="Tier">The effective tier now.</param>
/// <param name="Previous">The tier of the stored entitlement, which differs from <paramref name="Tier"/> when it has lapsed.</param>
/// <param name="Lowered">The actuators that fell back to notify-only, for example "heater of Kitchen herbs".</param>
public sealed record ClampResult(Tier Tier, Tier? Previous, IReadOnlyList<string> Lowered);

/// <summary>
/// When a person's tier drops, automation beyond what the new tier allows falls back to notify-only (D25). Safety stays on,
/// nothing is deleted and nothing running is left hanging. It runs when billing announces a lower tier (the plan-change
/// notification then says what was lowered) and, for a time-limited entitlement that simply ran out, in every control cycle.
/// </summary>
public sealed class TierClamp(GreenhausDbContext db, EntitlementService entitlements, TimeProvider clock)
{
    public async Task<ClampResult> ClampAsync(string userId, CancellationToken ct)
    {
        var tier = await entitlements.TierOfAsync(userId, ct);
        var previous = (await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.UserId == userId, ct))?.Tier;
        var pots = await db.Pots.Where(p => p.OwnerId == userId).Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        if (pots.Count == 0) return new ClampResult(tier, previous, []);

        var potIds = pots.Select(p => p.Id).ToList();
        var actuators = await db.Actuators.Where(a => potIds.Contains(a.PotId)).ToListAsync(ct);
        var lowered = new List<string>();
        foreach (var actuator in actuators)
        {
            var allowed = TierClampRules.Clamp(actuator.Kind, actuator.Mode, tier);
            if (allowed == actuator.Mode) continue;
            var from = actuator.Mode;
            actuator.Mode = allowed;
            // A suggestion for the old mode is not valid any more.
            actuator.SuggestedAt = null;
            db.AddAudit(actuator.PotId, "automation", "mode_changed",
                $"{from} -> {allowed}: the {tier} plan does not include automatic {ActuatorPolicy.MetricFor(actuator.Kind).ToString().ToLowerInvariant()} control".ToLowerInvariant(),
                actuator.Id, null, clock);
            lowered.Add($"{actuator.Kind.ToString().ToLowerInvariant()} of {pots.First(p => p.Id == actuator.PotId).Name}");
        }
        if (lowered.Count > 0) await db.SaveChangesAsync(ct);
        return new ClampResult(tier, previous, lowered);
    }
}