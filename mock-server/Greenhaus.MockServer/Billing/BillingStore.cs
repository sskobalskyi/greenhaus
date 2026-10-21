namespace Greenhaus.MockServer.Billing;

/// <summary>
/// Subscriptions, purchase grants and invoices of the demo user, with the rules the real billing service will have.
/// Time comes from the mock clock, so renewal and grace periods can be fast-forwarded.
/// </summary>
public class BillingStore(MockClock clock)
{
    public static readonly TimeSpan Period = TimeSpan.FromDays(30);
    public static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    public static readonly Dictionary<Tier, double> Prices = new() { [Tier.Novice] = 0, [Tier.Gardener] = 3, [Tier.Druid] = 7 };

    private readonly object _gate = new();
    private Subscription? _subscription;
    private string? _paymentToken;
    private readonly List<Grant> _grants = [];
    private readonly List<Invoice> _invoices = [];

    public object Sync => _gate;

    public void Reset()
    {
        lock (_gate)
        {
            _subscription = null;
            _paymentToken = null;
            _grants.Clear();
            _invoices.Clear();
            clock.Reset();
            // The demo user starts on a paid Gardener plan, like the Greenhaus mock assumes.
            Subscribe(Tier.Gardener, "tok-demo");
        }
    }

    public IReadOnlyList<Plan> Plans() =>
        [.. new[] { Tier.Novice, Tier.Gardener, Tier.Druid }.Select(t => new Plan { Tier = t, PricePerMonth = new Money { Amount = Prices[t], Currency = "USD" } })];

    /// <summary>Brings the state up to the clock: renews or ends the subscription, and revokes grants whose grace ran out.</summary>
    public void Advance()
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            if (_subscription is { Status: not "cancelled" } sub)
            {
                while (sub.CurrentPeriodEnd <= now && sub.Status != "cancelled")
                {
                    if (sub.CancelAtPeriodEnd)
                    {
                        sub.Status = "cancelled";
                        break;
                    }

                    var declined = _paymentToken?.EndsWith("-renewal-fails", StringComparison.Ordinal) == true;
                    AddInvoice(sub.CurrentPeriodEnd, Prices[sub.Tier], declined ? "failed" : "paid");
                    sub.Status = declined ? "past_due" : "active";
                    sub.CurrentPeriodEnd += Period;
                    if (declined) break;
                }
            }

            foreach (var grant in _grants.Where(g => g.Status == "at_risk" && g.GraceEndsAt <= now)) grant.Status = "revoked";
            foreach (var grant in _grants.Where(g => g.Status == "active" && g.ValidUntil <= now)) grant.Status = "revoked";
        }
    }

    public Subscription? Current()
    {
        Advance();
        lock (_gate) return _subscription;
    }

    public (Subscription? Subscription, string? Error) Subscribe(Tier tier, string token)
    {
        lock (_gate)
        {
            if (token.EndsWith("-declined", StringComparison.Ordinal))
            {
                AddInvoice(clock.UtcNow, Prices[tier], "failed");
                return (null, "payment_declined");
            }

            if (_subscription is { Status: "active" or "past_due" } existing && existing.Tier == tier && !existing.CancelAtPeriodEnd)
                return (null, "already_subscribed");

            _paymentToken = token;
            var now = clock.UtcNow;
            AddInvoice(now, Prices[tier], "paid");
            _subscription = new Subscription { Id = Guid.NewGuid(), Tier = tier, Status = "active", CurrentPeriodEnd = now + Period };
            return (_subscription, null);
        }
    }

    public Subscription? Cancel()
    {
        Advance();
        lock (_gate)
        {
            if (_subscription is not { Status: "active" or "past_due" } sub) return null;
            sub.CancelAtPeriodEnd = true;
            return sub;
        }
    }

    public IReadOnlyList<Grant> Grants()
    {
        Advance();
        lock (_gate) return [.. _grants.OrderByDescending(g => g.ValidFrom)];
    }

    public IReadOnlyList<Invoice> Invoices()
    {
        lock (_gate) return [.. _invoices.OrderByDescending(i => i.IssuedAt)];
    }

    // ---- mock-only controls that stand in for order events -----------------------------------------------

    public Grant AddGrant(Guid? orderId, Tier tier, int months)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            var grant = new Grant
            {
                Id = Guid.NewGuid(), OrderId = orderId ?? Guid.NewGuid(), Tier = tier, Status = "active",
                ValidFrom = now, ValidUntil = now + TimeSpan.FromDays(30 * months),
            };
            _grants.Add(grant);
            return grant;
        }
    }

    /// <summary>An order was returned or refunded: the grant is at risk until the grace period ends.</summary>
    public Grant? MarkAtRisk(Guid id)
    {
        lock (_gate)
        {
            var grant = _grants.FirstOrDefault(g => g.Id == id);
            if (grant is not { Status: "active" }) return grant;
            grant.Status = "at_risk";
            grant.GraceEndsAt = clock.UtcNow + Grace;
            return grant;
        }
    }

    /// <summary>The return was cancelled before the grace period ended.</summary>
    public Grant? Restore(Guid id)
    {
        lock (_gate)
        {
            var grant = _grants.FirstOrDefault(g => g.Id == id);
            if (grant is not { Status: "at_risk" }) return grant;
            grant.Status = "active";
            grant.GraceEndsAt = null;
            return grant;
        }
    }

    // ---- the effective tier -------------------------------------------------------------------------------

    /// <summary>The best of the paid subscription and the grants that still count. An at-risk grant counts until revoked.</summary>
    public Tier? EffectiveTier()
    {
        Advance();
        lock (_gate)
        {
            var tiers = new List<Tier>();
            if (_subscription is { Status: "active" or "past_due" } sub) tiers.Add(sub.Tier);
            tiers.AddRange(_grants.Where(g => g.Status is "active" or "at_risk").Select(g => g.Tier));
            return tiers.Count == 0 ? null : tiers.Max();
        }
    }

    private void AddInvoice(DateTimeOffset at, double amount, string status) =>
        _invoices.Add(new Invoice { Id = Guid.NewGuid(), IssuedAt = at, Amount = new Money { Amount = amount, Currency = "USD" }, Status = status });
}
