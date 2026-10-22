using Greenhaus.Billing.Messaging;
using Greenhaus.Billing.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Greenhaus.Billing.Infrastructure;

/// <summary>Something that happened to a user's billing while time moved on. The reason is the one entitlement events carry.</summary>
public sealed record Change(string UserId, string Reason, Guid? GrantId = null);

/// <summary>
/// Brings billing up to the clock: renews subscriptions or ends them, and ends grants whose grace or validity ran out.
/// The same code runs for one user when they call the API (so an answer is never stale) and for everyone from the background job.
/// </summary>
public sealed class BillingProcessor(BillingDbContext db, IOptions<BillingOptions> options, TimeProvider clock, EventPublisher events, EntitlementService entitlements)
{
    public async Task<List<Change>> AdvanceAsync(string? userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;

        var dueSubscriptions = db.Subscriptions.Where(s => s.Status != "cancelled" && s.CurrentPeriodEnd <= now);
        var dueGrants = db.Grants.Where(g => (g.Status == "at_risk" && g.GraceEndsAt <= now) || (g.Status == "active" && g.ValidUntil <= now));
        if (userId is not null)
        {
            dueSubscriptions = dueSubscriptions.Where(s => s.UserId == userId);
            dueGrants = dueGrants.Where(g => g.UserId == userId);
        }
        var subscriptions = await dueSubscriptions.ToListAsync(ct);
        var grants = await dueGrants.ToListAsync(ct);
        var changes = new List<Change>();
        if (subscriptions.Count == 0 && grants.Count == 0) return changes;

        // The state change, the events it causes and the new entitlement are saved together, or not at all.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var sub in subscriptions)
        {
            while (sub.Status != "cancelled" && sub.CurrentPeriodEnd <= now)
            {
                if (sub.CancelAtPeriodEnd)
                {
                    sub.Status = "cancelled";
                    events.SubscriptionChanged(sub.UserId, sub.Tier, "cancelled");
                    changes.Add(new Change(sub.UserId, "cancelled"));
                    break;
                }

                var declined = sub.PaymentToken.EndsWith("-renewal-fails", StringComparison.Ordinal);
                db.Invoices.Add(NewInvoice(sub.UserId, sub.CurrentPeriodEnd, sub.Tier, declined ? "failed" : "paid"));
                sub.Status = declined ? "past_due" : "active";
                sub.CurrentPeriodEnd += o.Period;
                if (declined)
                {
                    events.SubscriptionChanged(sub.UserId, sub.Tier, "past_due");
                    changes.Add(new Change(sub.UserId, "payment_failed"));
                    break;
                }
                changes.Add(new Change(sub.UserId, "subscribed"));
            }
        }

        foreach (var grant in grants)
        {
            var reason = grant.Status == "at_risk" ? "grant_revoked" : "grant_expired";
            grant.Status = "revoked";
            events.GrantChanged(grant);
            BillingTelemetry.GrantsChanged.Add(1, new KeyValuePair<string, object?>("status", "revoked"));
            changes.Add(new Change(grant.UserId, reason, grant.Id));
        }
        await db.SaveChangesAsync(ct);

        foreach (var user in changes.Select(c => c.UserId).Distinct())
            await entitlements.RecomputeAsync(user, changes.Last(c => c.UserId == user).Reason, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return changes;
    }
    public static InvoiceEntity NewInvoice(string userId, DateTimeOffset at, Tier tier, string status) =>
        new() { Id = Guid.NewGuid(), UserId = userId, IssuedAt = at, Amount = BillingRules.PricePerMonth(tier), Currency = "USD", Status = status };
}

/// <summary>Runs <see cref="BillingProcessor"/> for everyone on a timer.</summary>
public sealed class BillingTicker(IServiceScopeFactory scopes, IOptions<BillingOptions> options, ILogger<BillingTicker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BillingProcessor>().AdvanceAsync(null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning(e, "The billing tick failed, it will run again"); }
            await Task.Delay(options.Value.TickInterval, stoppingToken);
        }
    }
}