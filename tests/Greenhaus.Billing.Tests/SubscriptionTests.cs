using System.Net;
using System.Text.Json.Nodes;

namespace Greenhaus.Billing.Tests;

[Collection("billing")]
public class SubscriptionTests(BillingFixture billing)
{
    private Task<HttpResponseMessage> Call(string user, string method, string template, object? body = null) =>
        billing.Client.Call(method, template, null, body, null, BillingFixture.As(user));

    private async Task<JsonNode> Json(Task<HttpResponseMessage> response, HttpStatusCode expected)
    {
        var r = await response;
        Assert.Equal(expected, r.StatusCode);
        return await billing.Client.Node(r);
    }

    [Fact]
    public async Task A_walk_through_the_service_matches_the_contract()
    {
        var user = BillingFixture.NewUser();

        var plans = await Json(Call(user, "GET", "/plans"), HttpStatusCode.OK);
        Assert.Equal([0m, 3m, 7m], plans.AsArray().Select(p => p!["pricePerMonth"]!["amount"]!.GetValue<decimal>()));

        await Json(Call(user, "GET", "/subscription"), HttpStatusCode.NotFound);
        await Json(Call(user, "POST", "/subscription/cancel"), HttpStatusCode.NotFound);
        await Json(Call(user, "POST", "/subscription", new { tier = "novice", paymentMethodToken = "tok" }), HttpStatusCode.BadRequest);
        await Json(Call(user, "POST", "/subscription", new { tier = "gardener" }), HttpStatusCode.BadRequest);
        await Json(Call(user, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok-declined" }), HttpStatusCode.PaymentRequired);

        var created = await Json(Call(user, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok-demo" }), HttpStatusCode.Created);
        Assert.Equal("active", created["status"]!.GetValue<string>());
        await Json(Call(user, "GET", "/subscription"), HttpStatusCode.OK);
        var same = await Json(Call(user, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok-demo" }), HttpStatusCode.BadRequest);
        Assert.Equal("already_subscribed", same["code"]!.GetValue<string>());

        var upgraded = await Json(Call(user, "POST", "/subscription", new { tier = "druid", paymentMethodToken = "tok-demo" }), HttpStatusCode.Created);
        Assert.Equal("druid", upgraded["tier"]!.GetValue<string>());

        await Json(Call(user, "GET", "/grants"), HttpStatusCode.OK);
        var invoices = await Json(Call(user, "GET", "/invoices"), HttpStatusCode.OK);
        // A declined attempt is kept as a failed invoice, then two paid ones.
        Assert.Equal(["failed", "paid", "paid"], invoices.AsArray().Select(i => i!["status"]!.GetValue<string>()).Order());

        var cancelled = await Json(Call(user, "POST", "/subscription/cancel"), HttpStatusCode.OK);
        Assert.True(cancelled["cancelAtPeriodEnd"]!.GetValue<bool>());
        Assert.Equal("active", cancelled["status"]!.GetValue<string>());

        billing.Client.AssertCovers();
    }

    [Fact]
    public async Task Each_user_sees_only_their_own_subscription_and_invoices()
    {
        var a = BillingFixture.NewUser();
        var b = BillingFixture.NewUser();
        await Json(Call(a, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok" }), HttpStatusCode.Created);

        await Json(Call(b, "GET", "/subscription"), HttpStatusCode.NotFound);
        Assert.Empty((await Json(Call(b, "GET", "/invoices"), HttpStatusCode.OK)).AsArray());
    }

    [Fact]
    public async Task A_subscription_renews_each_period_with_an_invoice_and_keeps_the_period_going()
    {
        var user = BillingFixture.NewUser();
        var first = await Json(Call(user, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok" }), HttpStatusCode.Created);

        billing.Clock.Advance(TimeSpan.FromDays(65));
        var renewed = await Json(Call(user, "GET", "/subscription"), HttpStatusCode.OK);

        Assert.Equal("active", renewed["status"]!.GetValue<string>());
        Assert.True(renewed["currentPeriodEnd"]!.GetValue<DateTimeOffset>() > billing.Clock.GetUtcNow());
        // Paid at sign-up and at both renewals that fell due.
        var invoices = await Json(Call(user, "GET", "/invoices"), HttpStatusCode.OK);
        Assert.Equal(3, invoices.AsArray().Count(i => i!["status"]!.GetValue<string>() == "paid"));
        Assert.Equal(first["id"]!.GetValue<string>(), renewed["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failed_renewal_makes_the_subscription_past_due_with_a_failed_invoice()
    {
        var user = BillingFixture.NewUser();
        await Json(Call(user, "POST", "/subscription", new { tier = "druid", paymentMethodToken = "tok-renewal-fails" }), HttpStatusCode.Created);

        billing.Clock.Advance(TimeSpan.FromDays(31));
        var sub = await Json(Call(user, "GET", "/subscription"), HttpStatusCode.OK);

        Assert.Equal("past_due", sub["status"]!.GetValue<string>());
        var invoices = await Json(Call(user, "GET", "/invoices"), HttpStatusCode.OK);
        Assert.Contains(invoices.AsArray(), i => i!["status"]!.GetValue<string>() == "failed");
    }

    [Fact]
    public async Task A_cancelled_subscription_runs_to_the_end_of_the_period_and_then_ends()
    {
        var user = BillingFixture.NewUser();
        await Json(Call(user, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok" }), HttpStatusCode.Created);
        await Json(Call(user, "POST", "/subscription/cancel"), HttpStatusCode.OK);

        billing.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal("active", (await Json(Call(user, "GET", "/subscription"), HttpStatusCode.OK))["status"]!.GetValue<string>());

        billing.Clock.Advance(TimeSpan.FromDays(21));
        Assert.Equal("cancelled", (await Json(Call(user, "GET", "/subscription"), HttpStatusCode.OK))["status"]!.GetValue<string>());
        // Nothing is charged after cancelling, and cancelling again has nothing to cancel.
        Assert.Single((await Json(Call(user, "GET", "/invoices"), HttpStatusCode.OK)).AsArray());
        await Json(Call(user, "POST", "/subscription/cancel"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subscribing_again_after_cancelling_starts_a_fresh_period()
    {
        var user = BillingFixture.NewUser();
        await Json(Call(user, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok" }), HttpStatusCode.Created);
        await Json(Call(user, "POST", "/subscription/cancel"), HttpStatusCode.OK);

        var again = await Json(Call(user, "POST", "/subscription", new { tier = "gardener", paymentMethodToken = "tok" }), HttpStatusCode.Created);
        Assert.False(again["cancelAtPeriodEnd"]?.GetValue<bool>() ?? false);
    }
}