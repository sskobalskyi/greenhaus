using Greenhaus.ContractTesting;
using System.Net;
using System.Text.Json.Nodes;

namespace Greenhaus.MockServer.Tests;

public class BillingTests
{
    private static object Body(string tier, string token = "tok-card") => new { tier, paymentMethodToken = token };

    [Fact]
    public async Task Every_operation_of_the_billing_contract_is_served_and_matches_it()
    {
        using var api = new Api();
        var billing = api.Billing;

        Assert.Equal(HttpStatusCode.OK, (await billing.Call("GET", "/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await billing.Call("GET", "/subscription")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await billing.Call("POST", "/subscription", body: Body("druid"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await billing.Call("POST", "/subscription/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await billing.Call("GET", "/grants")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await billing.Call("GET", "/invoices")).StatusCode);

        billing.AssertCovers();
    }

    [Fact]
    public async Task Plans_are_public_and_the_free_tier_costs_nothing()
    {
        using var api = new Api();
        var plans = await api.Billing.Node(await api.Billing.Call("GET", "/plans"));
        Assert.Equal(3, plans.AsArray().Count);
        Assert.Equal(0, plans[0]!["pricePerMonth"]!["amount"]!.GetValue<double>());
        Assert.Equal(7, plans[2]!["pricePerMonth"]!["amount"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_declined_card_is_refused_and_the_plan_does_not_change()
    {
        using var api = new Api();
        var declined = await api.Billing.Call("POST", "/subscription", body: Body("druid", "tok-declined"));
        Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);
        Assert.Equal("payment_declined", (await api.Billing.Node(declined))["code"]!.GetValue<string>());

        var current = await api.Billing.Node(await api.Billing.Call("GET", "/subscription"));
        Assert.Equal("gardener", current["tier"]!.GetValue<string>());
        var invoices = await api.Billing.Node(await api.Billing.Call("GET", "/invoices"));
        Assert.Contains(invoices.AsArray(), i => i!["status"]!.GetValue<string>() == "failed");
    }

    [Fact]
    public async Task Subscribing_to_the_same_plan_twice_or_to_the_free_one_is_refused()
    {
        using var api = new Api();
        var same = await api.Billing.Call("POST", "/subscription", body: Body("gardener"));
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.Equal("already_subscribed", (await api.Billing.Node(same))["code"]!.GetValue<string>());

        var free = await api.Billing.Call("POST", "/subscription", body: Body("novice"));
        Assert.Equal(HttpStatusCode.BadRequest, free.StatusCode);
    }

    [Fact]
    public async Task Cancelling_keeps_the_plan_until_the_period_ends_and_then_ends_it()
    {
        using var api = new Api();
        var cancelled = await api.Billing.Node(await api.Billing.Call("POST", "/subscription/cancel"));
        Assert.True(cancelled["cancelAtPeriodEnd"]!.GetValue<bool>());
        Assert.Equal("active", cancelled["status"]!.GetValue<string>());

        await api.Advance(31);
        var after = await api.Billing.Node(await api.Billing.Call("GET", "/subscription"));
        Assert.Equal("cancelled", after["status"]!.GetValue<string>());

        // With nothing paid left, the Greenhaus entitlements fall back to the free tier.
        var entitlements = await api.Node(await api.Call("GET", "/me/entitlements"));
        Assert.Equal("novice", entitlements["tier"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_subscription_renews_each_period_and_issues_an_invoice()
    {
        using var api = new Api();
        var before = await api.Billing.Node(await api.Billing.Call("GET", "/subscription"));
        await api.Advance(31);
        var after = await api.Billing.Node(await api.Billing.Call("GET", "/subscription"));

        Assert.Equal("active", after["status"]!.GetValue<string>());
        Assert.True(DateTimeOffset.Parse(after["currentPeriodEnd"]!.GetValue<string>()) > DateTimeOffset.Parse(before["currentPeriodEnd"]!.GetValue<string>()));
        var invoices = await api.Billing.Node(await api.Billing.Call("GET", "/invoices"));
        Assert.Contains(invoices.AsArray(), i => i!["status"]!.GetValue<string>() == "paid");
    }

    [Fact]
    public async Task A_card_that_fails_on_renewal_makes_the_subscription_past_due()
    {
        using var api = new Api();
        await api.Billing.Call("POST", "/subscription", body: Body("druid", "tok-renewal-fails"));
        await api.Advance(31);

        var current = await api.Billing.Node(await api.Billing.Call("GET", "/subscription"));
        Assert.Equal("past_due", current["status"]!.GetValue<string>());
        var invoices = await api.Billing.Node(await api.Billing.Call("GET", "/invoices"));
        Assert.Contains(invoices.AsArray(), i => i!["status"]!.GetValue<string>() == "failed");
    }

    [Fact]
    public async Task Subscribing_changes_the_greenhaus_entitlements()
    {
        using var api = new Api();
        Assert.Equal("gardener", (await api.Node(await api.Call("GET", "/me/entitlements")))["tier"]!.GetValue<string>());

        await api.Billing.Call("POST", "/subscription", body: Body("druid"));
        var entitlements = await api.Node(await api.Call("GET", "/me/entitlements"));
        Assert.Equal("druid", entitlements["tier"]!.GetValue<string>());
        Assert.Null(entitlements["limits"]!["pots"]);
    }

    [Fact]
    public async Task A_returned_order_puts_its_grant_at_risk_and_the_grace_period_ends_it()
    {
        using var api = new Api();
        // Stands in for order.confirmed.v1: a purchase grants a plan.
        var created = await api.Billing.Node(await api.Mock("POST", "/mock/billing/grants", new { tier = "druid", months = 3 }));
        var id = created["id"]!.GetValue<string>();
        Assert.Equal("active", created["status"]!.GetValue<string>());
        Assert.Equal("druid", (await api.Node(await api.Call("GET", "/me/entitlements")))["tier"]!.GetValue<string>());

        // Stands in for order.returned.v1.
        var atRisk = await api.Billing.Node(await api.Mock("POST", $"/mock/billing/grants/{id}/return"));
        Assert.Equal("at_risk", atRisk["status"]!.GetValue<string>());
        Assert.NotNull(atRisk["graceEndsAt"]);

        var entitlements = await api.Node(await api.Call("GET", "/me/entitlements"));
        var source = entitlements["sources"]!.AsArray().First(s => s!["type"]!.GetValue<string>() == "purchase_grant")!;
        Assert.Equal("at_risk", source["status"]!.GetValue<string>());
        Assert.Equal("druid", entitlements["tier"]!.GetValue<string>()); // still counts during the grace period

        await api.Advance(8);
        var grants = await api.Billing.Node(await api.Billing.Call("GET", "/grants"));
        Assert.Equal("revoked", grants[0]!["status"]!.GetValue<string>());
        Assert.Equal("gardener", (await api.Node(await api.Call("GET", "/me/entitlements")))["tier"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_return_that_is_cancelled_in_time_restores_the_grant()
    {
        using var api = new Api();
        var created = await api.Billing.Node(await api.Mock("POST", "/mock/billing/grants", new { tier = "druid", months = 3 }));
        var id = created["id"]!.GetValue<string>();
        await api.Mock("POST", $"/mock/billing/grants/{id}/return");
        await api.Advance(3);

        var restored = await api.Billing.Node(await api.Mock("POST", $"/mock/billing/grants/{id}/restore"));
        Assert.Equal("active", restored["status"]!.GetValue<string>());
        await api.Advance(10);
        var grants = await api.Billing.Node(await api.Billing.Call("GET", "/grants"));
        Assert.Equal("active", grants[0]!["status"]!.GetValue<string>());
    }
}
