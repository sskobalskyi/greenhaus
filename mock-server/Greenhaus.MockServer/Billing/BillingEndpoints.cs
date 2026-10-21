namespace Greenhaus.MockServer.Billing;

public static class BillingEndpoints
{
    public static void MapBillingApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/billing/v1");

        api.MapGet("/plans", (BillingStore billing) => Results.Ok(billing.Plans()));

        api.MapGet("/subscription", (BillingStore billing) =>
            billing.Current() is { } subscription ? Results.Ok(subscription) : Problems.Fail(404, "no_subscription", "No subscription"));

        api.MapPost("/subscription", (SubscribeRequest body, BillingStore billing) =>
        {
            if (body.Tier is null || string.IsNullOrWhiteSpace(body.PaymentMethodToken))
                return Problems.Fail(400, "validation_failed", "A tier and a payment method token are required");
            if (body.Tier == Tier.Novice)
                return Problems.Fail(400, "validation_failed", "The Novice tier is free, cancel the subscription instead");

            billing.Advance();
            var (subscription, error) = billing.Subscribe(body.Tier.Value, body.PaymentMethodToken);
            return error switch
            {
                "payment_declined" => Problems.Fail(402, "payment_declined", "The payment was declined"),
                "already_subscribed" => Problems.Fail(400, "already_subscribed", "Already on this plan"),
                _ => Results.Json(subscription, statusCode: 201),
            };
        });

        api.MapPost("/subscription/cancel", (BillingStore billing) =>
            billing.Cancel() is { } subscription ? Results.Ok(subscription) : Problems.Fail(404, "no_subscription", "No subscription to cancel"));

        api.MapGet("/grants", (BillingStore billing) => Results.Ok(billing.Grants()));
        api.MapGet("/invoices", (BillingStore billing) => Results.Ok(billing.Invoices()));

        // ---- mock-only controls, not part of the contract ---------------------------------------------------
        // Real grants come from order events. These stand in for them so a demo or a test can create and age them.
        var mock = app.MapGroup("/mock");

        mock.MapPost("/billing/grants", (GrantRequest body, BillingStore billing) =>
        {
            if (body.Tier is null or Tier.Novice) return Problems.Fail(400, "validation_failed", "A paid tier is required");
            return Results.Json(billing.AddGrant(body.OrderId, body.Tier.Value, Math.Clamp(body.Months ?? 3, 1, 24)), statusCode: 201);
        });

        mock.MapPost("/billing/grants/{id:guid}/return", (Guid id, BillingStore billing) =>
            billing.MarkAtRisk(id) is { } grant ? Results.Ok(grant) : Problems.NotFound("Unknown grant"));

        mock.MapPost("/billing/grants/{id:guid}/restore", (Guid id, BillingStore billing) =>
            billing.Restore(id) is { } grant ? Results.Ok(grant) : Problems.NotFound("Unknown grant"));

        mock.MapPost("/clock/advance", (AdvanceRequest body, MockClock clock) =>
        {
            if (body.Days is null or <= 0 or > 400) return Problems.Fail(400, "validation_failed", "Days must be between 0 and 400");
            clock.Advance(TimeSpan.FromDays(body.Days.Value));
            return Results.Ok(new { now = clock.UtcNow });
        });
    }
}
