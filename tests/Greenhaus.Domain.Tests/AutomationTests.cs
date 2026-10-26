using Greenhaus.Domain;

namespace Greenhaus.Domain.Tests;

public class AutomationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static RequirementDraft Target(Metric metric, double? min, double? max = null, double? margin = 2) => new(metric, true, min, max, margin);

    private static Dictionary<Metric, RequirementDraft> Targets(params RequirementDraft[] drafts) => drafts.ToDictionary(d => d.Metric);

    private static Dictionary<Metric, MetricSnapshot> Readings(Metric metric, double value, int ageMinutes = 1) =>
        new() { [metric] = new MetricSnapshot([value], Now.AddMinutes(-ageMinutes)) };

    private static ActuatorSnapshot Pump(ControlMode mode = ControlMode.Auto, DateTimeOffset? lastRunAt = null, int lastRunSeconds = 10, int runToday = 0,
        int fruitless = 0, double? startValue = null, bool blocked = false, bool calibrated = true, bool tankEmpty = false, DateTimeOffset? runningUntil = null,
        int? maxFruitless = 3, int interval = 3600, int cap = 120) =>
        new(ActuatorKind.Pump, mode, new SafetyLimits(20, interval, cap, maxFruitless), lastRunAt, lastRunSeconds, runningUntil, runToday, fruitless, startValue,
            blocked, ThermalCutoffDeclared: false, calibrated, tankEmpty);

    private static ActuatorSnapshot Of(ActuatorKind kind, ControlMode mode = ControlMode.Auto, bool cutoff = true, DateTimeOffset? runningUntil = null, int maxRun = 900) =>
        new(kind, mode, new SafetyLimits(maxRun, 600, 7200), null, 0, runningUntil, 0, 0, null, false, cutoff, true, false);

    private static readonly Dictionary<Metric, RequirementDraft> Soil = Targets(Target(Metric.SoilMoisture, 40, 70));

    [Fact]
    public void A_dry_pot_is_watered_once_for_a_bounded_time()
    {
        var decision = AutomationRules.Decide(Pump(), false, Soil, Readings(Metric.SoilMoisture, 25), Now);

        Assert.Equal(AutomationAction.Run, decision.Action);
        Assert.Equal(10, decision.Seconds);
        Assert.Equal("automation", decision.Actor);
        Assert.Equal("needs_water", decision.Code);
        Assert.Equal(25, decision.StartValue);
        Assert.Contains("Soil moisture is 25", decision.Reason);
    }

    [Fact]
    public void A_run_never_exceeds_the_actuators_own_limit()
    {
        var pump = Pump() with { Limits = new SafetyLimits(4, 3600, 120, 3) };
        Assert.Equal(4, AutomationRules.Decide(pump, false, Soil, Readings(Metric.SoilMoisture, 25), Now).Seconds);
    }

    [Fact]
    public void A_pot_inside_its_range_gets_nothing()
    {
        var decision = AutomationRules.Decide(Pump(), false, Soil, Readings(Metric.SoilMoisture, 55), Now);
        Assert.Equal(AutomationAction.None, decision.Action);
        Assert.False(decision.Demand);
        Assert.False(decision.Record);
    }

    [Fact]
    public void The_minimum_interval_is_counted_from_the_end_of_the_last_run()
    {
        var lastRun = Now.AddSeconds(-1800);
        var tooSoon = AutomationRules.Decide(Pump(lastRunAt: lastRun), false, Soil, Readings(Metric.SoilMoisture, 25), Now);
        Assert.Equal(AutomationAction.None, tooSoon.Action);
        Assert.True(tooSoon.Demand);
        Assert.False(tooSoon.Record);

        // 10 s of watering plus the 3600 s interval have passed.
        var later = AutomationRules.Decide(Pump(lastRunAt: Now.AddSeconds(-3611), startValue: 20), false, Soil, Readings(Metric.SoilMoisture, 25), Now);
        Assert.Equal(AutomationAction.Run, later.Action);
    }

    [Fact]
    public void The_daily_cap_shortens_the_last_run_and_then_stops_the_pump()
    {
        var almost = AutomationRules.Decide(Pump(runToday: 115), false, Soil, Readings(Metric.SoilMoisture, 25), Now);
        Assert.Equal(AutomationAction.Run, almost.Action);
        Assert.Equal(5, almost.Seconds);

        var used = AutomationRules.Decide(Pump(runToday: 120), false, Soil, Readings(Metric.SoilMoisture, 25), Now);
        Assert.Equal(AutomationAction.None, used.Action);
        Assert.Equal("daily_cap", used.Code);
        Assert.Equal("safety", used.Actor);
        Assert.True(used.Record);
    }

    [Fact]
    public void Runs_that_change_nothing_block_the_pump_instead_of_emptying_the_tank_forever()
    {
        // The last run started at 25 and the pot is still at 25.5: not enough movement.
        var stalled = Pump(lastRunAt: Now.AddSeconds(-4000), startValue: 25, fruitless: 1);
        var second = AutomationRules.Decide(stalled, false, Soil, Readings(Metric.SoilMoisture, 25.5), Now);
        Assert.Equal(AutomationAction.Run, second.Action);
        Assert.Equal(2, second.FruitlessRuns);

        var third = AutomationRules.Decide(stalled with { FruitlessRuns = 2 }, false, Soil, Readings(Metric.SoilMoisture, 25.5), Now);
        Assert.Equal(AutomationAction.Block, third.Action);
        Assert.Equal("dry_run", third.Code);
        Assert.Equal("safety", third.Actor);
        Assert.Equal(3, third.FruitlessRuns);
    }

    [Fact]
    public void A_run_that_worked_resets_the_counter()
    {
        var worked = Pump(lastRunAt: Now.AddSeconds(-4000), startValue: 25, fruitless: 2);
        var decision = AutomationRules.Decide(worked, false, Soil, Readings(Metric.SoilMoisture, 38), Now);
        Assert.Equal(AutomationAction.Run, decision.Action);
        Assert.Equal(0, decision.FruitlessRuns);
    }

    [Fact]
    public void The_users_own_fruitless_limit_wins()
    {
        var stalled = Pump(lastRunAt: Now.AddSeconds(-4000), startValue: 25, fruitless: 0, maxFruitless: 1);
        Assert.Equal(AutomationAction.Block, AutomationRules.Decide(stalled, false, Soil, Readings(Metric.SoilMoisture, 25), Now).Action);
    }

    [Fact]
    public void A_blocked_actuator_waits_for_the_user()
    {
        var decision = AutomationRules.Decide(Pump(blocked: true), false, Soil, Readings(Metric.SoilMoisture, 10), Now);
        Assert.Equal(AutomationAction.None, decision.Action);
        Assert.False(decision.Record);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(600)]
    public void An_old_reading_means_nothing_is_done_and_it_is_said(int ageMinutes)
    {
        var decision = AutomationRules.Decide(Pump(), false, Soil, Readings(Metric.SoilMoisture, 10, ageMinutes), Now);
        Assert.Equal(AutomationAction.None, decision.Action);
        Assert.Equal("sensor_stale", decision.Code);
        Assert.Equal("safety", decision.Actor);
        Assert.True(decision.Record);
    }

    [Fact]
    public void A_missing_reading_counts_as_stale_and_a_channel_without_one_makes_the_metric_stale()
    {
        var none = AutomationRules.Decide(Pump(), false, Soil, new Dictionary<Metric, MetricSnapshot>(), Now);
        Assert.Equal("sensor_stale", none.Code);

        var oneSilent = new Dictionary<Metric, MetricSnapshot> { [Metric.SoilMoisture] = new([10, 55], DateTimeOffset.MinValue) };
        Assert.Equal("sensor_stale", AutomationRules.Decide(Pump(), false, Soil, oneSilent, Now).Code);
    }

    [Fact]
    public void The_driest_zone_decides_and_zones_are_not_averaged()
    {
        var zones = new Dictionary<Metric, MetricSnapshot> { [Metric.SoilMoisture] = new([25, 65], Now.AddMinutes(-1)) };
        var decision = AutomationRules.Decide(Pump(), false, Soil, zones, Now);
        Assert.Equal(AutomationAction.Run, decision.Action);
        Assert.Equal(25, decision.StartValue);
    }

    [Fact]
    public void An_uncalibrated_sensor_and_an_empty_tank_keep_the_pump_off_and_say_why()
    {
        var uncalibrated = AutomationRules.Decide(Pump(calibrated: false), false, Soil, Readings(Metric.SoilMoisture, 25), Now);
        Assert.Equal("calibration_required", uncalibrated.Code);
        Assert.Equal("safety", uncalibrated.Actor);

        var empty = AutomationRules.Decide(Pump(tankEmpty: true), false, Soil, Readings(Metric.SoilMoisture, 25), Now);
        Assert.Equal("tank_empty", empty.Code);
        Assert.Equal(AutomationAction.None, empty.Action);
    }

    [Fact]
    public void A_heater_without_a_declared_thermal_cutoff_never_runs_automatically()
    {
        var targets = Targets(Target(Metric.Temperature, 18, 26));
        var cold = Readings(Metric.Temperature, 12);

        var refused = AutomationRules.Decide(Of(ActuatorKind.Heater, cutoff: false), false, targets, cold, Now);
        Assert.Equal("thermal_cutoff_required", refused.Code);
        Assert.Equal(AutomationAction.None, refused.Action);

        var allowed = AutomationRules.Decide(Of(ActuatorKind.Heater), false, targets, cold, Now);
        Assert.Equal(AutomationAction.Run, allowed.Action);
        Assert.Equal(300, allowed.Seconds);
    }

    [Fact]
    public void The_fan_handles_too_hot_and_too_little_air()
    {
        var hot = AutomationRules.Decide(Of(ActuatorKind.Fan), false, Targets(Target(Metric.Temperature, 18, 26)), Readings(Metric.Temperature, 31), Now);
        Assert.Equal(AutomationAction.Run, hot.Action);
        Assert.Contains("above the target 26", hot.Reason);

        var still = AutomationRules.Decide(Of(ActuatorKind.Fan), false, Targets(Target(Metric.Airflow, 0.5, 2)), Readings(Metric.Airflow, 0.1), Now);
        Assert.Equal(AutomationAction.Run, still.Action);

        // A heater-range pot that is only a little warm is left alone.
        var fine = AutomationRules.Decide(Of(ActuatorKind.Fan), false, Targets(Target(Metric.Temperature, 18, 26)), Readings(Metric.Temperature, 25), Now);
        Assert.Equal(AutomationAction.None, fine.Action);
    }

    [Fact]
    public void The_lamp_runs_when_it_is_dark_and_stops_once_the_light_is_back_above_the_range_plus_margin()
    {
        var targets = Targets(Target(Metric.Light, 1000, null, margin: 200));

        var dark = AutomationRules.Decide(Of(ActuatorKind.Lamp), false, targets, Readings(Metric.Light, 300), Now);
        Assert.Equal(AutomationAction.Run, dark.Action);
        Assert.Equal(900, dark.Seconds);

        var running = Of(ActuatorKind.Lamp, runningUntil: Now.AddMinutes(30));
        // Still below the range, or inside the margin: the lamp keeps running and no second command is sent.
        Assert.Equal(AutomationAction.None, AutomationRules.Decide(running, false, targets, Readings(Metric.Light, 700), Now).Action);
        Assert.Equal(AutomationAction.None, AutomationRules.Decide(running, false, targets, Readings(Metric.Light, 1100), Now).Action);
        // Above range plus margin: stop.
        var stop = AutomationRules.Decide(running, false, targets, Readings(Metric.Light, 1300), Now);
        Assert.Equal(AutomationAction.Stop, stop.Action);
        Assert.Equal("target_reached", stop.Code);
    }

    [Fact]
    public void The_humidifier_runs_when_the_air_is_too_dry()
    {
        var decision = AutomationRules.Decide(Of(ActuatorKind.Humidifier), false, Targets(Target(Metric.AirHumidity, 40, 70)), Readings(Metric.AirHumidity, 25), Now);
        Assert.Equal(AutomationAction.Run, decision.Action);
        Assert.Equal("needs_humidity", decision.Code);
    }

    [Fact]
    public void The_kill_switch_and_the_off_mode_stop_everything()
    {
        Assert.Equal(AutomationAction.None, AutomationRules.Decide(Pump(), killSwitch: true, Soil, Readings(Metric.SoilMoisture, 5), Now).Action);
        Assert.Equal(AutomationAction.None, AutomationRules.Decide(Pump(ControlMode.Off), false, Soil, Readings(Metric.SoilMoisture, 5), Now).Action);
    }

    [Fact]
    public void Notify_mode_suggests_but_never_runs_anything()
    {
        var decision = AutomationRules.Decide(Pump(ControlMode.Notify), false, Soil, Readings(Metric.SoilMoisture, 25), Now);
        Assert.Equal(AutomationAction.Suggest, decision.Action);
        Assert.Equal(0, decision.Seconds);
        Assert.True(decision.Demand);
        Assert.Contains("Run the pump", decision.Reason);
    }

    [Fact]
    public void A_metric_the_user_does_not_track_gives_the_actuator_nothing_to_do()
    {
        var off = Targets(new RequirementDraft(Metric.SoilMoisture, false, 40, 70, 2));
        Assert.Equal(AutomationAction.None, AutomationRules.Decide(Pump(), false, off, Readings(Metric.SoilMoisture, 5), Now).Action);
        Assert.Equal(AutomationAction.None, AutomationRules.Decide(Pump(), false, new Dictionary<Metric, RequirementDraft>(), Readings(Metric.SoilMoisture, 5), Now).Action);
    }
}

public class TierClampTests
{
    [Theory]
    [InlineData(ActuatorKind.Pump, ControlMode.Auto, Tier.Novice, ControlMode.Auto)]
    [InlineData(ActuatorKind.Lamp, ControlMode.Auto, Tier.Novice, ControlMode.Auto)]
    [InlineData(ActuatorKind.Heater, ControlMode.Auto, Tier.Novice, ControlMode.Notify)]
    [InlineData(ActuatorKind.Fan, ControlMode.Auto, Tier.Novice, ControlMode.Notify)]
    [InlineData(ActuatorKind.Heater, ControlMode.Auto, Tier.Gardener, ControlMode.Auto)]
    [InlineData(ActuatorKind.Heater, ControlMode.Off, Tier.Novice, ControlMode.Off)]
    [InlineData(ActuatorKind.Humidifier, ControlMode.Notify, Tier.Novice, ControlMode.Notify)]
    public void A_lower_tier_keeps_what_it_allows_and_lowers_the_rest_without_switching_things_off(ActuatorKind kind, ControlMode mode, Tier tier, ControlMode expected) =>
        Assert.Equal(expected, TierClampRules.Clamp(kind, mode, tier));
}
