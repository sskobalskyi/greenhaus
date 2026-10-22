using Greenhaus.Billing.Auth;
using Greenhaus.Billing.Contracts;
using Greenhaus.Billing.Infrastructure;
using Greenhaus.Billing.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Billing.Features;

public static class BillingEndpoints
{
    private static readonly Tier[] Tiers = [Tier.Novice, Tier.Gardener, Tier.Druid];

    public static void MapBilling(this RouteGroupBuilder api)
    {
        // Public, so prices are visible before sign-up.
        api.MapGet("/plans", () =>
            Results.Ok(Tiers.Select(t => new PlanDto { Tier = t, PricePerMonth = new MoneyDto { Amount = BillingRules.PricePerMonth(t), Currency = "USD" } }).ToList()))
            .AllowAnonymous();

        api.MapGet("/subscription", async (HttpContext http, BillingDbContext db, BillingProcessor processor, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            await processor.AdvanceAsync(user, ct);
            return await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == user, ct) is { } sub
                ? Results.Ok(sub.ToDto())
                : Problems.Fail(404, "no_subscription", "No subscription");
        });

        api.MapPost("/subscription", async (SubscribeRequest body, HttpContext http, BillingDbContext db, BillingProcessor processor, EventPublisher events, EntitlementService entitlements, Microsoft.Extensions.Options.IOptions<BillingOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            if (body.Tier is null || string.IsNullOrWhiteSpace(body.PaymentMethodToken))
                return Problems.Invalid("A tier and a payment method token are required");
            if (body.Tier == Tier.Novice)
                return Problems.Invalid("The Novice tier is free, cancel the subscription instead");

            var user = http.User.UserId();
            var tier = body.Tier.Value;
            await processor.AdvanceAsync(user, ct);
            var now = clock.GetUtcNow();

            if (body.PaymentMethodToken.EndsWith("-declined", StringComparison.Ordinal))
            {
                db.Invoices.Add(BillingProcessor.NewInvoice(user, now, tier, "failed"));
                await db.SaveChangesAsync(ct);
                return Problems.Fail(402, "payment_declined", "The payment was declined");
            }

            var existing = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == user, ct);
            if (existing is { Status: "active" or "past_due", CancelAtPeriodEnd: false } && existing.Tier == tier)
                return Problems.Fail(400, "already_subscribed", "Already on this plan");

            if (existing is null) db.Subscriptions.Add(existing = new SubscriptionEntity { UserId = user, PaymentToken = body.PaymentMethodToken, Status = "active" });
            existing.Id = Guid.NewGuid();
            existing.Tier = tier;
            existing.Status = "active";
            existing.CancelAtPeriodEnd = false;
            existing.PaymentToken = body.PaymentMethodToken;
            existing.CreatedAt = now;
            existing.CurrentPeriodEnd = now + options.Value.Period;
            db.Invoices.Add(BillingProcessor.NewInvoice(user, now, tier, "paid"));
            events.SubscriptionChanged(user, tier, "active");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await entitlements.RecomputeAsync(user, "subscribed", ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Json(existing.ToDto(), statusCode: 201);
        });

        api.MapPost("/subscription/cancel", async (HttpContext http, BillingDbContext db, BillingProcessor processor, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            await processor.AdvanceAsync(user, ct);
            if (await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == user, ct) is not { Status: "active" or "past_due" } sub)
                return Problems.Fail(404, "no_subscription", "No subscription to cancel");
            sub.CancelAtPeriodEnd = true;
            await db.SaveChangesAsync(ct);
            return Results.Ok(sub.ToDto());
        });

        api.MapGet("/grants", async (HttpContext http, BillingDbContext db, BillingProcessor processor, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            await processor.AdvanceAsync(user, ct);
            var grants = await db.Grants.AsNoTracking().Where(g => g.UserId == user).OrderByDescending(g => g.ValidFrom).ToListAsync(ct);
            return Results.Ok(grants.Select(g => g.ToDto()).ToList());
        });

        api.MapGet("/invoices", async (HttpContext http, BillingDbContext db, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            var invoices = await db.Invoices.AsNoTracking().Where(i => i.UserId == user).OrderByDescending(i => i.IssuedAt).ToListAsync(ct);
            return Results.Ok(invoices.Select(i => i.ToDto()).ToList());
        });
    }

    public static SubscriptionDto ToDto(this SubscriptionEntity s) =>
        new() { Id = s.Id, Tier = s.Tier, Status = s.Status, CurrentPeriodEnd = s.CurrentPeriodEnd, CancelAtPeriodEnd = s.CancelAtPeriodEnd };

    public static GrantDto ToDto(this GrantEntity g) =>
        new() { Id = g.Id, OrderId = g.OrderId, Tier = g.Tier, Status = g.Status, ValidFrom = g.ValidFrom, ValidUntil = g.ValidUntil, GraceEndsAt = g.GraceEndsAt };

    public static InvoiceDto ToDto(this InvoiceEntity i) =>
        new() { Id = i.Id, IssuedAt = i.IssuedAt, Amount = new MoneyDto { Amount = i.Amount, Currency = i.Currency }, Status = i.Status };
}