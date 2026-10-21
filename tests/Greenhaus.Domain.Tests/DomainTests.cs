using Greenhaus.Domain;

namespace Greenhaus.Domain.Tests;

public class TierPolicyTests
{
    [Fact]
    public void The_free_tier_automates_watering_and_light_but_not_the_rest()
    {
        var novice = TierPolicy.LimitsFor(Tier.Novice);
        Assert.Equal(ControlMode.Auto, novice.MaxModeFor(Metric.SoilMoisture));
        Assert.Equal(ControlMode.Auto, novice.MaxModeFor(Metric.Light));
        Assert.Equal(ControlMode.Notify, novice.MaxModeFor(Metric.Temperature));
        Assert.Equal(ControlMode.Notify, novice.MaxModeFor(Metric.Ph));
    }

    [Fact]
    public void Limits_grow_with_the_tier_and_the_top_tier_has_no_pot_limit()
    {
        var limits = TierPolicy.Tiers.Select(TierPolicy.LimitsFor).ToList();
        Assert.Equal([2, 6], limits.Take(2).Select(l => l.Pots!.Value));
        Assert.Null(limits[2].Pots);
        Assert.True(limits[0].ZoneGridMax < limits[1].ZoneGridMax && limits[1].ZoneGridMax < limits[2].ZoneGridMax);
        Assert.True(limits[0].AiDataAccess < limits[1].AiDataAccess && limits[1].AiDataAccess < limits[2].AiDataAccess);
    }

    [Theory]
    [InlineData(Metric.Temperature, Tier.Gardener)]
    [InlineData(Metric.SoilMoisture, Tier.Novice)]
    [InlineData(Metric.Airflow, Tier.Gardener)]
    public void The_lowest_tier_allowing_auto_is_found(Metric metric, Tier expected) =>
        Assert.Equal(expected, TierPolicy.LowestTierAllowingAuto(metric));

    [Fact]
    public void Next_tier_walks_up_and_stops_at_the_top()
    {
        Assert.Equal(Tier.Gardener, TierPolicy.Next(Tier.Novice));
        Assert.Equal(Tier.Druid, TierPolicy.Next(Tier.Gardener));
        Assert.Equal(Tier.Druid, TierPolicy.Next(Tier.Druid));
    }
}

public class RequirementsSuggesterTests
{
    private static PlantPreference Plant(Guid id, double tMin, double tMax, double sMin, double sMax) => new(id, new Dictionary<Metric, Range>
    {
        [Metric.Temperature] = new(tMin, tMax),
        [Metric.SoilMoisture] = new(sMin, sMax),
    });

    [Fact]
    public void The_suggestion_is_the_intersection_of_the_ranges()
    {
        var result = RequirementsSuggester.Suggest([Plant(Guid.NewGuid(), 18, 30, 45, 65), Plant(Guid.NewGuid(), 15, 27, 55, 75)]);
        var temperature = result.Requirements.Single(r => r.Metric == Metric.Temperature);
        Assert.Equal((18d, 27d), (temperature.Min!.Value, temperature.Max!.Value));
        var soil = result.Requirements.Single(r => r.Metric == Metric.SoilMoisture);
        Assert.Equal((55d, 65d), (soil.Min!.Value, soil.Max!.Value));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Plants_that_disagree_are_reported_and_the_first_range_is_kept()
    {
        var basil = Guid.NewGuid();
        var cactus = Guid.NewGuid();
        var result = RequirementsSuggester.Suggest([Plant(basil, 18, 30, 45, 65), Plant(cactus, 10, 35, 5, 20)]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(Metric.SoilMoisture, conflict.Metric);
        Assert.Equal([basil, cactus], conflict.Plants);
        var soil = result.Requirements.Single(r => r.Metric == Metric.SoilMoisture);
        Assert.Equal((45d, 65d), (soil.Min!.Value, soil.Max!.Value));
    }

    [Fact]
    public void A_range_that_overlaps_in_one_point_is_not_a_conflict()
    {
        var result = RequirementsSuggester.Suggest([Plant(Guid.NewGuid(), 18, 30, 45, 65), Plant(Guid.NewGuid(), 30, 35, 5, 65)]);
        Assert.Empty(result.Conflicts);
        var temperature = result.Requirements.Single(r => r.Metric == Metric.Temperature);
        Assert.Equal(30d, temperature.Min);
        Assert.Equal(30d, temperature.Max);
    }

    [Fact]
    public void Metrics_no_plant_cares_about_are_off_and_ph_is_never_switched_on_by_itself()
    {
        var preferences = new[] { new PlantPreference(Guid.NewGuid(), new Dictionary<Metric, Range> { [Metric.Ph] = new(6, 7), [Metric.Light] = new(1000, 2000) }) };
        var result = RequirementsSuggester.Suggest(preferences);
        Assert.False(result.Requirements.Single(r => r.Metric == Metric.Temperature).Enabled);
        Assert.False(result.Requirements.Single(r => r.Metric == Metric.Ph).Enabled);
        Assert.True(result.Requirements.Single(r => r.Metric == Metric.Light).Enabled);
    }

    [Fact]
    public void A_pot_without_plants_suggests_nothing()
    {
        var result = RequirementsSuggester.Suggest([]);
        Assert.All(result.Requirements, r => Assert.False(r.Enabled));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Requirements_with_a_minimum_above_a_maximum_or_a_repeated_metric_are_invalid()
    {
        Assert.False(RequirementsSuggester.IsValid([new(Metric.Light, true, 5, 1, null)], out _));
        Assert.False(RequirementsSuggester.IsValid([new(Metric.Light, true, 1, 5, null), new(Metric.Light, true, 1, 5, null)], out _));
        Assert.True(RequirementsSuggester.IsValid([new(Metric.Light, true, 1, 5, null), new(Metric.Ph, false, null, null, null)], out _));
    }
}

public class ReadingEvaluatorTests
{
    private static readonly RequirementDraft Soil = new(Metric.SoilMoisture, true, 55, 65, 1);

    [Theory]
    [InlineData(60, ReadingState.Ok, null)]
    [InlineData(55, ReadingState.Ok, null)]
    [InlineData(65, ReadingState.Ok, null)]
    [InlineData(41, ReadingState.Attention, "below")]
    [InlineData(70, ReadingState.Attention, "above")]
    public void A_reading_is_compared_with_the_target(double value, ReadingState state, string? direction) =>
        Assert.Equal((state, direction), ReadingEvaluator.Evaluate(value, Soil));

    [Fact]
    public void A_metric_that_is_not_measured_is_never_a_problem() =>
        Assert.Equal(ReadingState.Ok, ReadingEvaluator.Evaluate(999, Soil with { Enabled = false }).State);

    [Fact]
    public void The_worst_state_wins_and_nothing_known_is_unknown()
    {
        Assert.Equal(ReadingState.Attention, ReadingEvaluator.Worst([ReadingState.Ok, ReadingState.Attention, ReadingState.Offline]));
        Assert.Equal(ReadingState.Offline, ReadingEvaluator.Worst([ReadingState.Ok, ReadingState.Offline]));
        Assert.Equal(ReadingState.Ok, ReadingEvaluator.Worst([ReadingState.Ok, ReadingState.Unknown]));
        Assert.Equal(ReadingState.Unknown, ReadingEvaluator.Worst([ReadingState.Unknown]));
        Assert.Equal(ReadingState.Unknown, ReadingEvaluator.Worst([]));
    }
}

public class ActuatorPolicyTests
{
    private static readonly SafetyLimits Hard = new(60, 600, 600);

    [Theory]
    [InlineData(ActuatorKind.Pump, Metric.SoilMoisture)]
    [InlineData(ActuatorKind.Heater, Metric.Temperature)]
    [InlineData(ActuatorKind.Lamp, Metric.Light)]
    [InlineData(ActuatorKind.Fan, Metric.Airflow)]
    [InlineData(ActuatorKind.Humidifier, Metric.AirHumidity)]
    public void An_actuator_acts_on_its_own_metric(ActuatorKind kind, Metric metric) => Assert.Equal(metric, ActuatorPolicy.MetricFor(kind));

    [Fact]
    public void Default_limits_are_cautious_and_never_looser_than_the_controllers_own()
    {
        var pump = ActuatorPolicy.DefaultLimits(ActuatorKind.Pump, Hard);
        Assert.True(ActuatorPolicy.WithinHardLimits(pump, Hard));
        Assert.Equal(20, pump.MaxRunSeconds);

        var tiny = new SafetyLimits(5, 7200, 30);
        var capped = ActuatorPolicy.DefaultLimits(ActuatorKind.Pump, tiny);
        Assert.True(ActuatorPolicy.WithinHardLimits(capped, tiny));
    }

    [Fact]
    public void Limits_may_be_tightened_but_not_loosened()
    {
        Assert.True(ActuatorPolicy.WithinHardLimits(new SafetyLimits(30, 1200, 300), Hard));
        Assert.False(ActuatorPolicy.WithinHardLimits(new SafetyLimits(61, 600, 600), Hard));
        Assert.False(ActuatorPolicy.WithinHardLimits(new SafetyLimits(30, 300, 600), Hard)); // interval shorter than the controller allows
        Assert.False(ActuatorPolicy.WithinHardLimits(new SafetyLimits(30, 600, 601), Hard));
        Assert.False(ActuatorPolicy.WithinHardLimits(new SafetyLimits(0, 600, 600), Hard));
    }

    [Theory]
    [InlineData(ActuatorKind.Pump, ControlMode.Auto, Tier.Novice, false, true, ModeCheck.Allowed)]
    [InlineData(ActuatorKind.Pump, ControlMode.Auto, Tier.Novice, false, false, ModeCheck.CalibrationRequired)]
    [InlineData(ActuatorKind.Heater, ControlMode.Auto, Tier.Novice, true, true, ModeCheck.TierRequired)]
    [InlineData(ActuatorKind.Heater, ControlMode.Auto, Tier.Druid, false, true, ModeCheck.ThermalCutoffRequired)]
    [InlineData(ActuatorKind.Heater, ControlMode.Auto, Tier.Druid, true, true, ModeCheck.Allowed)]
    [InlineData(ActuatorKind.Heater, ControlMode.Notify, Tier.Novice, false, false, ModeCheck.Allowed)]
    [InlineData(ActuatorKind.Lamp, ControlMode.Auto, Tier.Novice, false, false, ModeCheck.Allowed)]
    public void Auto_mode_is_guarded_by_tier_thermal_cutoff_and_calibration(ActuatorKind kind, ControlMode mode, Tier tier, bool cutoff, bool calibrated, ModeCheck expected) =>
        Assert.Equal(expected, ActuatorPolicy.CheckMode(kind, mode, tier, cutoff, calibrated));

    [Fact]
    public void The_tier_is_checked_before_the_safety_guards()
    {
        // A novice asking for an automated heater is told about the tier, not about the cutoff.
        Assert.Equal(ModeCheck.TierRequired, ActuatorPolicy.CheckMode(ActuatorKind.Heater, ControlMode.Auto, Tier.Novice, false, false));
    }
}

public class SpeciesCatalogTests
{
    private static string CatalogPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "data", "species.json"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "data", "species.json");
    }

    [Fact]
    public void The_real_catalog_loads_with_thirty_species_and_ranges_that_make_sense()
    {
        var catalog = SpeciesCatalog.Load(CatalogPath());
        Assert.True(catalog.All.Count >= 30);
        Assert.Equal(catalog.All.Count, catalog.All.Select(s => s.Id).Distinct().Count());
        Assert.All(catalog.All, s => Assert.All(s.Preferred.Values, r => Assert.True(r.Min <= r.Max)));
    }

    [Fact]
    public void Species_can_be_found_by_id_and_searched_by_either_name()
    {
        var catalog = SpeciesCatalog.Load(CatalogPath());
        Assert.NotNull(catalog.Find("basil"));
        Assert.Null(catalog.Find("nope"));
        Assert.Contains(catalog.Search("MONSTERA"), s => s.Id == "monstera");
        Assert.Contains(catalog.Search("ocimum"), s => s.Id == "basil");
        Assert.Equal(catalog.All.Count, catalog.Search(null).Count);
    }

    [Fact]
    public void Metric_names_round_trip()
    {
        foreach (var metric in Enum.GetValues<Metric>())
        {
            Assert.True(MetricNames.TryParse(MetricNames.Key(metric), out var parsed));
            Assert.Equal(metric, parsed);
        }
        Assert.False(MetricNames.TryParse("mood", out _));
        Assert.Equal("soil_moisture", MetricNames.Key(Metric.SoilMoisture));
    }
}