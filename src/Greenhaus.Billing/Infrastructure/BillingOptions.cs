using Greenhaus.Domain;

namespace Greenhaus.Billing.Infrastructure;

public sealed class GrantRuleOptions
{
    public decimal MinTotal { get; set; }
    public Tier Tier { get; set; }
    public int Months { get; set; }
}

public sealed class BillingOptions
{
    /// <summary>One billing period. Shorten it to watch renewals run, the demo clock of the contract.</summary>
    public TimeSpan Period { get; set; } = BillingRules.DefaultPeriod;
    /// <summary>How long a grant stays at risk after a return or refund before it is revoked.</summary>
    public TimeSpan Grace { get; set; } = BillingRules.DefaultGrace;
    /// <summary>How long after a failed payment billing tries the payment method again, while the subscription is past due.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromDays(1);
    /// <summary>How long a subscription may stay past due. When it ends and no retry has worked, the subscription is cancelled.</summary>
    public TimeSpan DunningPeriod { get; set; } = TimeSpan.FromDays(7);
    /// <summary>How often the background job renews subscriptions and ends grants.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Order totals and what they earn. Empty means the placeholder thresholds of the spec.</summary>
    public List<GrantRuleOptions> Grants { get; set; } = [];

    public IReadOnlyList<GrantRule> GrantRules =>
        Grants.Count == 0 ? GrantPolicy.Defaults : [.. Grants.Select(g => new GrantRule(g.MinTotal, g.Tier, g.Months))];
}