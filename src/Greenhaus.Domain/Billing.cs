namespace Greenhaus.Domain;

/// <summary>Prices and periods of the paid plans. Amounts are in US dollars.</summary>
public static class BillingRules
{
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromDays(7);

    public static decimal PricePerMonth(Tier tier) => tier switch { Tier.Gardener => 3m, Tier.Druid => 7m, _ => 0m };
}

/// <summary>An order of at least <see cref="MinTotal"/> earns <see cref="Months"/> months of <see cref="Tier"/>.</summary>
public sealed record GrantRule(decimal MinTotal, Tier Tier, int Months);

public static class GrantPolicy
{
    /// <summary>Placeholder thresholds of the spec: $30 for 3 months of Gardener, $80 for 12, $150 for 6 months of Druid.</summary>
    public static readonly IReadOnlyList<GrantRule> Defaults =
        [new(30m, Tier.Gardener, 3), new(80m, Tier.Gardener, 12), new(150m, Tier.Druid, 6)];

    /// <summary>The grant an order total earns: the rule with the highest threshold the total reaches, or null below the lowest.</summary>
    public static GrantRule? For(decimal total, IEnumerable<GrantRule> rules) =>
        rules.Where(r => total >= r.MinTotal).OrderByDescending(r => r.MinTotal).FirstOrDefault();
}

public static class SourceStatus
{
    public const string Active = "active";
    public const string PastDue = "past_due";
    public const string AtRisk = "at_risk";
}

/// <summary>Something that gives a user a tier for a while: a paid subscription or a purchase grant.</summary>
public sealed record EntitlementSource(string Kind, Tier Tier, DateTimeOffset From, DateTimeOffset Until, string Status);

/// <summary>The tier a user has now, until when, and which kind of source and status it comes from.</summary>
public sealed record EffectiveEntitlement(Tier Tier, DateTimeOffset? Until, string? Kind, string? Status);

public static class Entitlements
{
    /// <summary>
    /// The effective entitlement is the best tier among the sources that count now. Between sources of that tier a healthy one
    /// (active) wins over one in trouble (past due, at risk). Active sources of the best tier that follow each other are chained,
    /// so a second grant bought while the first runs extends the end instead of stacking a second tier on top.
    /// </summary>
    public static EffectiveEntitlement Compute(IEnumerable<EntitlementSource> sources, DateTimeOffset now)
    {
        var counted = sources.Where(s => s.From <= now && s.Until > now).ToList();
        if (counted.Count == 0) return new EffectiveEntitlement(Tier.Novice, null, null, null);

        var best = counted.Max(s => s.Tier);
        var ofBest = counted.Where(s => s.Tier == best).ToList();
        var primary = ofBest.OrderByDescending(s => s.Status == SourceStatus.Active).ThenByDescending(s => s.Until).First();
        if (primary.Status != SourceStatus.Active) return new EffectiveEntitlement(best, primary.Until, primary.Kind, primary.Status);

        var until = ofBest.Where(s => s.Status == SourceStatus.Active).Max(s => s.Until);
        var chain = sources.Where(s => s.Tier == best && s.Status == SourceStatus.Active).ToList();
        // A source that starts when (or before) the current end continues it.
        for (var extended = true; extended;)
        {
            extended = false;
            foreach (var next in chain.Where(s => s.From <= until && s.Until > until))
            {
                until = next.Until;
                extended = true;
            }
        }
        var winner = ofBest.Where(s => s.Status == SourceStatus.Active).OrderByDescending(s => s.Until).First();
        return new EffectiveEntitlement(best, until, winner.Kind, SourceStatus.Active);
    }
}