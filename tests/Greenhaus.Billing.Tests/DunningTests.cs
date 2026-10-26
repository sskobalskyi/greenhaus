using System.Net;
using System.Text.Json.Nodes;
using Greenhaus.Billing.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Billing.Tests;

/// <summary>
/// What happens when a subscription's payment fails: billing tries the payment method again, and when the dunning period is over
/// without a payment the subscription is cancelled and the tier falls back. The default timings are used (retry every day, seven
/// days of dunning) with a fake clock; every response is checked against the contract and every event against the AsyncAPI document.
/// </summary>
[Collection("billing")]
public class DunningTests(BillingFixture billing)
{
    private Task<HttpResponseMessage> Call(string user, string method, string template, object? body = null) =>
        billing.Client.Call(method, template, null, body, null, BillingFixture.As(user));

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return await billing.Client.Node(r);
    }

    private async Task<string> Status(string user) => (await Json(Call(user, "GET", "/subscription"), HttpStatusCode.OK))["status"]!.GetValue<string>();

    private async Task Subscribe(string user, string tier, string token) =>
        await Json(Call(user, "POST", "/subscription", new { tier, paymentMethodToken = token }), HttpStatusCode.Created);

    /// <summary>The events billing wrote for a user, oldest first, each checked against the AsyncAPI document.</summary>
    private async Task<List<(string Type, JsonNode Body)>> EventsOf(string user)
    {
        var rows = await billing.WithDb(db => db.Outbox.OrderBy(m => m.CreatedAt).ToListAsync());
        var mine = rows.Where(m => m.Payload.Contains(user)).ToList();
        foreach (var row in mine) billing.Events.VerifyPayload(row.Type, row.Payload);
        return [.. mine.Select(m => (m.Type, JsonNode.Parse(m.Payload)!))];
    }

    private async Task<List<string>> InvoiceStatuses(string user) =>
        [.. (await Json(Call(user, "GET", "/invoices"), HttpStatusCode.OK)).AsArray().Select(i => i!["status"]!.GetValue<string>())];

    [Fact]
    public async Task A_retry_that_works_brings_a_past_due_subscription_back_without_a_cancel()
    {
        var user = BillingFixture.NewUser();
        // The demo payment method that fails the first renewal and works when billing tries again.
        await Subscribe(user, "druid", "tok-renewal-recovers");

        billing.Clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromHours(1));
        Assert.Equal("past_due", await Status(user));

        // The retry falls due a day after the failed payment.
        billing.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal("active", await Status(user));

        var invoices = await InvoiceStatuses(user);
        Assert.Equal(["failed", "paid", "paid"], invoices.Order());

        var events = await EventsOf(user);
        var subscription = events.Where(e => e.Type == "subscription.changed.v1").Select(e => e.Body["status"]!.GetValue<string>()).ToList();
        Assert.Equal(["active", "past_due", "active"], subscription);
        Assert.DoesNotContain(events, e => e.Type == "subscription.changed.v1" && e.Body["status"]!.GetValue<string>() == "cancelled");
        // The tier never fell: the person stays on Druid while billing retries.
        Assert.All(events.Where(e => e.Type == "entitlement.changed.v1"), e => Assert.Equal("druid", e.Body["tier"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_payment_that_never_works_is_retried_daily_and_the_subscription_is_cancelled_when_dunning_ends()
    {
        var user = BillingFixture.NewUser();
        await Subscribe(user, "gardener", "tok-renewal-fails");

        billing.Clock.Advance(TimeSpan.FromDays(33));
        Assert.Equal("past_due", await Status(user));

        // Dunning started at the failed payment on day 30 and lasts seven days.
        billing.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal("past_due", await Status(user));
        billing.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal("cancelled", await Status(user));

        // The first failed renewal and the daily retries are failed invoices, and nothing was paid after sign-up.
        var invoices = await InvoiceStatuses(user);
        Assert.Equal(1, invoices.Count(s => s == "paid"));
        Assert.True(invoices.Count(s => s == "failed") >= 5, string.Join(",", invoices));

        var events = await EventsOf(user);
        Assert.Equal(["active", "past_due", "cancelled"], events.Where(e => e.Type == "subscription.changed.v1").Select(e => e.Body["status"]!.GetValue<string>()));

        // With no grant the tier falls to the free one, and the reason says the subscription was cancelled.
        var last = events.Last(e => e.Type == "entitlement.changed.v1").Body;
        Assert.Equal("novice", last["tier"]!.GetValue<string>());
        Assert.Equal("gardener", last["previousTier"]!.GetValue<string>());
        Assert.Equal("cancelled", last["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_a_purchase_grant_the_tier_falls_back_to_the_grants_tier_not_to_novice()
    {
        var user = BillingFixture.NewUser();
        await Subscribe(user, "druid", "tok-renewal-fails");
        var now = billing.Clock.GetUtcNow();
        await billing.WithDb(async db =>
        {
            db.Grants.Add(new GrantEntity { Id = Guid.NewGuid(), UserId = user, OrderId = Guid.NewGuid(), Tier = Tier.Gardener, Status = "active", ValidFrom = now, ValidUntil = now.AddDays(365) });
            await db.SaveChangesAsync();
            return 0;
        });

        billing.Clock.Advance(TimeSpan.FromDays(40));
        Assert.Equal("cancelled", await Status(user));

        var last = (await EventsOf(user)).Last(e => e.Type == "entitlement.changed.v1").Body;
        Assert.Equal("gardener", last["tier"]!.GetValue<string>());
        Assert.Equal("druid", last["previousTier"]!.GetValue<string>());
        // The tier now comes from the grant, so the reason names the grant for the API to label the source.
        Assert.Equal("grant_started", last["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_job_is_idempotent_and_a_cancelled_subscription_is_announced_once()
    {
        var user = BillingFixture.NewUser();
        await Subscribe(user, "gardener", "tok-renewal-fails");

        billing.Clock.Advance(TimeSpan.FromDays(40));
        Assert.Equal("cancelled", await Status(user));
        var before = await EventsOf(user);
        var invoicesBefore = await InvoiceStatuses(user);

        billing.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal("cancelled", await Status(user));
        Assert.Equal("cancelled", await Status(user));

        Assert.Equal(before.Count, (await EventsOf(user)).Count);
        Assert.Equal(invoicesBefore.Count, (await InvoiceStatuses(user)).Count);
        Assert.Single(before, e => e.Type == "subscription.changed.v1" && e.Body["status"]!.GetValue<string>() == "cancelled");
    }

    [Fact]
    public async Task Subscribing_again_after_the_dunning_cancel_works()
    {
        var user = BillingFixture.NewUser();
        await Subscribe(user, "gardener", "tok-renewal-fails");
        billing.Clock.Advance(TimeSpan.FromDays(40));
        Assert.Equal("cancelled", await Status(user));

        var again = await Json(Call(user, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok-good" }), HttpStatusCode.Created);
        Assert.Equal("active", again["status"]!.GetValue<string>());
        Assert.True(again["currentPeriodEnd"]!.GetValue<DateTimeOffset>() > billing.Clock.GetUtcNow());

        // The fresh subscription has no dunning clock left over from the old one.
        billing.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal("active", await Status(user));
    }

    [Fact]
    public async Task A_person_can_replace_the_payment_method_of_a_past_due_subscription_by_subscribing_to_the_same_plan()
    {
        var user = BillingFixture.NewUser();
        await Subscribe(user, "gardener", "tok-renewal-fails");
        billing.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal("past_due", await Status(user));

        var fixedUp = await Json(Call(user, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok-new-card" }), HttpStatusCode.Created);
        Assert.Equal("active", fixedUp["status"]!.GetValue<string>());

        // The old dunning clock is gone: well past the original deadline the subscription is still active.
        billing.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal("active", await Status(user));
    }

    [Fact]
    public async Task Cancelling_twice_is_harmless_and_a_subscription_cancelled_while_past_due_ends_at_its_period_end()
    {
        var user = BillingFixture.NewUser();
        await Subscribe(user, "gardener", "tok-demo");
        await Json(Call(user, "POST", "/subscription/cancel"), HttpStatusCode.OK);
        var second = await Json(Call(user, "POST", "/subscription/cancel"), HttpStatusCode.OK);
        Assert.True(second["cancelAtPeriodEnd"]!.GetValue<bool>());

        var other = BillingFixture.NewUser();
        await Subscribe(other, "gardener", "tok-renewal-fails");
        billing.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal("past_due", await Status(other));
        var flagged = await Json(Call(other, "POST", "/subscription/cancel"), HttpStatusCode.OK);
        Assert.True(flagged["cancelAtPeriodEnd"]!.GetValue<bool>());
        Assert.Equal("past_due", flagged["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Subscribing_while_a_grant_gives_a_higher_tier_keeps_the_higher_tier()
    {
        var user = BillingFixture.NewUser();
        var now = billing.Clock.GetUtcNow();
        await billing.WithDb(async db =>
        {
            db.Grants.Add(new GrantEntity { Id = Guid.NewGuid(), UserId = user, OrderId = Guid.NewGuid(), Tier = Tier.Druid, Status = "active", ValidFrom = now, ValidUntil = now.AddDays(180) });
            await db.SaveChangesAsync();
            return 0;
        });

        await Subscribe(user, "gardener", "tok-demo");

        var tiers = (await EventsOf(user)).Where(e => e.Type == "entitlement.changed.v1").Select(e => e.Body["tier"]!.GetValue<string>()).ToList();
        Assert.Equal(["druid"], tiers);
    }
}
