using Greenhaus.Domain;

namespace Greenhaus.Domain.Tests;

public class BillingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static EntitlementSource Source(string kind, Tier tier, double fromDays, double untilDays, string status = SourceStatus.Active) =>
        new(kind, tier, Now.AddDays(fromDays), Now.AddDays(untilDays), status);

    [Theory]
    [InlineData(10, null, 0)]
    [InlineData(29.99, null, 0)]
    [InlineData(30, Tier.Gardener, 3)]
    [InlineData(79.99, Tier.Gardener, 3)]
    [InlineData(80, Tier.Gardener, 12)]
    [InlineData(149.99, Tier.Gardener, 12)]
    [InlineData(150, Tier.Druid, 6)]
    [InlineData(2000, Tier.Druid, 6)]
    public void An_order_total_earns_the_rule_with_the_highest_threshold_it_reaches(double total, Tier? tier, int months)
    {
        var rule = GrantPolicy.For((decimal)total, GrantPolicy.Defaults);
        Assert.Equal(tier, rule?.Tier);
        Assert.Equal(months, rule?.Months ?? 0);
    }

    [Fact]
    public void With_no_source_the_user_is_on_the_free_tier()
    {
        var effective = Entitlements.Compute([], Now);
        Assert.Equal(Tier.Novice, effective.Tier);
        Assert.Null(effective.Until);
    }

    [Fact]
    public void The_best_tier_wins_and_sources_that_ended_or_have_not_started_do_not_count()
    {
        var effective = Entitlements.Compute(
        [
            Source("subscription", Tier.Gardener, -10, 20),
            Source("grant", Tier.Druid, -1, 90),
            Source("grant", Tier.Druid, 5, 400),
            Source("grant", Tier.Druid, -100, -1),
        ], Now);
        Assert.Equal(Tier.Druid, effective.Tier);
        // The second Druid grant starts before the first ends, so it continues it.
        Assert.Equal(Now.AddDays(400), effective.Until);
    }

    [Fact]
    public void Grants_of_the_same_tier_extend_instead_of_stacking()
    {
        var effective = Entitlements.Compute([Source("grant", Tier.Gardener, -10, 80), Source("grant", Tier.Gardener, 80, 440)], Now);
        Assert.Equal(Tier.Gardener, effective.Tier);
        Assert.Equal(Now.AddDays(440), effective.Until);
    }

    [Fact]
    public void A_gap_between_grants_is_not_bridged()
    {
        var effective = Entitlements.Compute([Source("grant", Tier.Gardener, -10, 80), Source("grant", Tier.Gardener, 100, 200)], Now);
        Assert.Equal(Now.AddDays(80), effective.Until);
    }

    [Fact]
    public void A_healthy_source_of_the_same_tier_wins_over_one_that_is_at_risk()
    {
        var effective = Entitlements.Compute(
            [Source("grant", Tier.Gardener, -10, 7, SourceStatus.AtRisk), Source("subscription", Tier.Gardener, -5, 25)], Now);
        Assert.Equal(SourceStatus.Active, effective.Status);
        Assert.Equal("subscription", effective.Kind);
    }

    [Fact]
    public void A_grant_at_risk_counts_until_its_grace_ends_and_says_so()
    {
        var effective = Entitlements.Compute([Source("grant", Tier.Gardener, -10, 7, SourceStatus.AtRisk)], Now);
        Assert.Equal(Tier.Gardener, effective.Tier);
        Assert.Equal(SourceStatus.AtRisk, effective.Status);
        Assert.Equal(Now.AddDays(7), effective.Until);
    }

    [Fact]
    public void A_higher_tier_at_risk_still_beats_a_lower_healthy_one_until_it_ends()
    {
        var effective = Entitlements.Compute(
            [Source("grant", Tier.Druid, -10, 3, SourceStatus.AtRisk), Source("subscription", Tier.Gardener, -5, 25)], Now);
        Assert.Equal(Tier.Druid, effective.Tier);
        Assert.Equal(SourceStatus.AtRisk, effective.Status);
    }
}