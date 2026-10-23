namespace Greenhaus.Domain;

public enum AutomationAction { None, Run, Stop, Suggest, Block }

/// <summary>The latest readings of one metric across the pot's active channels. A zone is never averaged: the loop acts on the worst one.</summary>
/// <param name="Values">One value per channel.</param>
/// <param name="OldestAt">When the least recent of those readings arrived. A channel with no reading at all gives <see cref="DateTimeOffset.MinValue"/>.</param>
public sealed record MetricSnapshot(IReadOnlyList<double> Values, DateTimeOffset OldestAt);

/// <summary>Everything the control decision needs to know about one actuator, as stored.</summary>
public sealed record ActuatorSnapshot(
    ActuatorKind Kind,
    ControlMode Mode,
    SafetyLimits Limits,
    DateTimeOffset? LastRunAt,
    int LastRunSeconds,
    DateTimeOffset? RunningUntil,
    int RunSecondsToday,
    int FruitlessRuns,
    double? LastRunStartValue,
    bool Blocked,
    bool ThermalCutoffDeclared,
    bool SensorsCalibrated,
    bool TankEmpty);

/// <summary>What the loop should do for one actuator, and why.</summary>
/// <param name="Code">A stable key for the reason, for example <c>needs_water</c> or <c>daily_cap</c>.</param>
/// <param name="Actor">`automation` for what the loop does, `safety` for a guard that stopped it. Values of the audit log contract.</param>
/// <param name="Demand">Whether the pot needs something from this actuator right now. Ends an episode when it turns false.</param>
/// <param name="Record">Whether the decision belongs in the audit log (a quiet "nothing to do" does not).</param>
public sealed record AutomationDecision(
    AutomationAction Action,
    int Seconds,
    string Code,
    string Reason,
    string Actor,
    bool Demand,
    bool Record,
    int FruitlessRuns,
    double? StartValue)
{
    public static AutomationDecision Quiet(bool demand = false, int fruitlessRuns = 0) =>
        new(AutomationAction.None, 0, "none", "", "automation", demand, false, fruitlessRuns, null);

    public static AutomationDecision Safety(string code, string reason, bool demand = true, int fruitlessRuns = 0) =>
        new(AutomationAction.None, 0, code, reason, "safety", demand, true, fruitlessRuns, null);
}

/// <summary>
/// The control loop's policy, as pure code. It reads the pot's requirements and the live readings and decides when an
/// actuator should run, within the actuator's safety limits. Pulse never learns why a pump ran.
/// </summary>
public static class AutomationRules
{
    /// <summary>A reading older than this means the controller stopped reporting.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>How many runs may leave the metric unmoved before the actuator is blocked, unless the user set a number.</summary>
    public const int DefaultMaxFruitlessRuns = 3;

    private enum Direction { Below, Above }

    private sealed record Trigger(Metric Metric, Direction Direction);

    private static IReadOnlyList<Trigger> TriggersFor(ActuatorKind kind) => kind switch
    {
        ActuatorKind.Pump => [new(Metric.SoilMoisture, Direction.Below)],
        ActuatorKind.Heater => [new(Metric.Temperature, Direction.Below)],
        ActuatorKind.Lamp => [new(Metric.Light, Direction.Below)],
        // Too hot is the fan's job (D17), and so is too little air.
        ActuatorKind.Fan => [new(Metric.Temperature, Direction.Above), new(Metric.Airflow, Direction.Below)],
        _ => [new(Metric.AirHumidity, Direction.Below)],
    };

    /// <summary>How long one automatic run lasts at most, before the actuator's own limit applies.</summary>
    public static int DefaultRunSeconds(ActuatorKind kind) => kind switch
    {
        ActuatorKind.Pump => 10,
        ActuatorKind.Heater => 300,
        ActuatorKind.Lamp => 3600,
        ActuatorKind.Fan => 600,
        _ => 300,
    };

    /// <summary>Whether a run moved the metric towards the target by enough to count as having worked.</summary>
    private static bool Improved(Direction direction, double before, double after, RequirementDraft target)
    {
        var width = target.Min is { } min && target.Max is { } max ? max - min : Math.Abs(target.Min ?? target.Max ?? 0);
        var threshold = Math.Max(0.5, 0.05 * width);
        return direction == Direction.Below ? after - before >= threshold : before - after >= threshold;
    }

    public static AutomationDecision Decide(
        ActuatorSnapshot actuator,
        bool killSwitch,
        IReadOnlyDictionary<Metric, RequirementDraft> targets,
        IReadOnlyDictionary<Metric, MetricSnapshot> readings,
        DateTimeOffset now)
    {
        // Off does nothing, the kill switch stops everything, and a blocked actuator waits for the user.
        if (actuator.Mode == ControlMode.Off || killSwitch || actuator.Blocked) return AutomationDecision.Quiet();

        var evaluated = new List<(Trigger Trigger, RequirementDraft Target, double Value, bool Demand, bool Satisfied)>();
        var stale = new List<Trigger>();
        foreach (var trigger in TriggersFor(actuator.Kind))
        {
            if (!targets.TryGetValue(trigger.Metric, out var target) || !target.Enabled) continue;
            var bound = trigger.Direction == Direction.Below ? target.Min : target.Max;
            if (bound is null) continue;

            if (!readings.TryGetValue(trigger.Metric, out var snapshot) || snapshot.Values.Count == 0 || now - snapshot.OldestAt > StaleAfter)
            {
                stale.Add(trigger);
                continue;
            }

            var margin = target.Margin ?? 0;
            // The worst zone decides: the driest for "below", the hottest for "above".
            var value = trigger.Direction == Direction.Below ? snapshot.Values.Min() : snapshot.Values.Max();
            var demand = trigger.Direction == Direction.Below ? value < bound : value > bound;
            var satisfied = trigger.Direction == Direction.Below ? value >= bound + margin : value <= bound - margin;
            evaluated.Add((trigger, target, value, demand, satisfied));
        }

        var demanding = evaluated.Where(e => e.Demand).ToList();
        var running = actuator.RunningUntil is { } until && until > now;

        if (evaluated.Count == 0 && stale.Count > 0)
            return AutomationDecision.Safety("sensor_stale", $"No recent {Name(stale[0].Metric)} reading, so nothing was done", demand: false);
        if (evaluated.Count == 0) return AutomationDecision.Quiet();

        // A running actuator is stopped as soon as everything it was running for is back inside the range plus its margin.
        if (running)
        {
            return evaluated.All(e => e.Satisfied)
                ? new AutomationDecision(AutomationAction.Stop, 0, "target_reached", $"{Capital(Name(evaluated[0].Trigger.Metric))} is back in range, stopped", "automation", false, true, actuator.FruitlessRuns, null)
                : AutomationDecision.Quiet(demand: demanding.Count > 0, fruitlessRuns: actuator.FruitlessRuns);
        }

        if (demanding.Count == 0) return AutomationDecision.Quiet(fruitlessRuns: actuator.FruitlessRuns);
        var first = demanding[0];
        var what = Name(first.Trigger.Metric);
        var relation = first.Trigger.Direction == Direction.Below ? "below" : "above";
        var limit = first.Trigger.Direction == Direction.Below ? first.Target.Min!.Value : first.Target.Max!.Value;

        if (actuator.Mode == ControlMode.Notify)
            return new AutomationDecision(AutomationAction.Suggest, 0, "suggest",
                $"{Capital(what)} is {Format(first.Value)}, {relation} the target {Format(limit)}. Run the {actuator.Kind.ToString().ToLowerInvariant()} to fix it", "automation", true, true, actuator.FruitlessRuns, null);

        // Auto from here on.
        if (actuator.Kind == ActuatorKind.Heater && !actuator.ThermalCutoffDeclared)
            return AutomationDecision.Safety("thermal_cutoff_required", "A heater does not run automatically without a declared hardware thermal cutoff");
        if (actuator.Kind == ActuatorKind.Pump && !actuator.SensorsCalibrated)
            return AutomationDecision.Safety("calibration_required", "The soil moisture sensor is not calibrated, so the pump does not run automatically");
        if (actuator.Kind == ActuatorKind.Pump && actuator.TankEmpty)
            return AutomationDecision.Safety("tank_empty", "The water tank is empty, so the pump does not run");

        var lastEnd = actuator.LastRunAt?.AddSeconds(actuator.LastRunSeconds);
        if (lastEnd is { } end && now < end.AddSeconds(actuator.Limits.MinIntervalSeconds))
            return AutomationDecision.Quiet(demand: true, fruitlessRuns: actuator.FruitlessRuns);

        var remaining = actuator.Limits.DailyCapSeconds - actuator.RunSecondsToday;
        if (remaining <= 0)
            return AutomationDecision.Safety("daily_cap", $"The daily limit of {actuator.Limits.DailyCapSeconds} s for the {actuator.Kind.ToString().ToLowerInvariant()} is used up", fruitlessRuns: actuator.FruitlessRuns);

        // Did the previous run move the metric? If not, count it, and stop the loop from watering an empty tank.
        var fruitless = actuator.FruitlessRuns;
        if (actuator.LastRunAt is not null && actuator.LastRunStartValue is { } startValue)
            fruitless = Improved(first.Trigger.Direction, startValue, first.Value, first.Target) ? 0 : fruitless + 1;
        var allowed = actuator.Limits.MaxFruitlessRuns ?? DefaultMaxFruitlessRuns;
        if (fruitless >= allowed)
            return new AutomationDecision(AutomationAction.Block, 0, "dry_run",
                $"{fruitless} runs in a row did not change the {what}. The {actuator.Kind.ToString().ToLowerInvariant()} is stopped, check it (an empty tank or a broken part)", "safety", true, true, fruitless, null);

        var seconds = Math.Min(Math.Min(actuator.Limits.MaxRunSeconds, DefaultRunSeconds(actuator.Kind)), remaining);
        return new AutomationDecision(AutomationAction.Run, seconds, DemandCode(actuator.Kind),
            $"{Capital(what)} is {Format(first.Value)}, {relation} the target {Format(limit)}: {Verb(actuator.Kind)} for {seconds} s", "automation", true, true, fruitless, first.Value);
    }

    private static string DemandCode(ActuatorKind kind) => kind switch
    {
        ActuatorKind.Pump => "needs_water",
        ActuatorKind.Heater => "needs_heat",
        ActuatorKind.Lamp => "needs_light",
        ActuatorKind.Fan => "needs_air",
        _ => "needs_humidity",
    };

    private static string Verb(ActuatorKind kind) => kind switch
    {
        ActuatorKind.Pump => "watering",
        ActuatorKind.Heater => "heating",
        ActuatorKind.Lamp => "light on",
        ActuatorKind.Fan => "fan on",
        _ => "humidifying",
    };

    private static string Name(Metric metric) => MetricNames.Key(metric).Replace('_', ' ');
    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];
    private static string Format(double value) => Math.Round(value, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What a tier allows an actuator to keep doing when the plan changes (D25: safety stays on, automation beyond the tier falls back).</summary>
public static class TierClampRules
{
    /// <summary>The mode an actuator may keep on a tier: whatever it has, lowered to what the tier allows for its metric.</summary>
    public static ControlMode Clamp(ActuatorKind kind, ControlMode mode, Tier tier)
    {
        var max = TierPolicy.LimitsFor(tier).MaxModeFor(ActuatorPolicy.MetricFor(kind));
        return mode > max ? max : mode;
    }
}
