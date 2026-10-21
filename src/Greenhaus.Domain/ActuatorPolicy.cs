namespace Greenhaus.Domain;

public sealed record SafetyLimits(int MaxRunSeconds, int MinIntervalSeconds, int DailyCapSeconds, int? MaxFruitlessRuns = null);

public enum ModeCheck { Allowed, TierRequired, ThermalCutoffRequired, CalibrationRequired }

public static class ActuatorPolicy
{
    /// <summary>The metric an actuator acts on: the pump waters, the heater and the fan treat temperature or airflow, and so on.</summary>
    public static Metric MetricFor(ActuatorKind kind) => kind switch
    {
        ActuatorKind.Pump => Metric.SoilMoisture,
        ActuatorKind.Heater => Metric.Temperature,
        ActuatorKind.Lamp => Metric.Light,
        ActuatorKind.Fan => Metric.Airflow,
        _ => Metric.AirHumidity,
    };

    /// <summary>Settings start no looser than a cautious default, and never beyond what the controller itself enforces.</summary>
    public static SafetyLimits DefaultLimits(ActuatorKind kind, SafetyLimits hard) => kind switch
    {
        ActuatorKind.Pump => new SafetyLimits(Math.Min(20, hard.MaxRunSeconds), Math.Max(3600, hard.MinIntervalSeconds), Math.Min(120, hard.DailyCapSeconds), 3),
        ActuatorKind.Heater => new SafetyLimits(Math.Min(900, hard.MaxRunSeconds), Math.Max(600, hard.MinIntervalSeconds), Math.Min(7200, hard.DailyCapSeconds)),
        _ => hard,
    };

    /// <summary>Limits can only be tightened inside the controller's own hard limits.</summary>
    public static bool WithinHardLimits(SafetyLimits requested, SafetyLimits hard) =>
        requested.MaxRunSeconds >= 1 && requested.DailyCapSeconds >= 1 && requested.MinIntervalSeconds >= 0
        && requested.MaxRunSeconds <= hard.MaxRunSeconds && requested.DailyCapSeconds <= hard.DailyCapSeconds && requested.MinIntervalSeconds >= hard.MinIntervalSeconds;

    /// <summary>
    /// Whether an actuator may be put in a mode. Auto needs the tier, a heater needs a declared hardware thermal cutoff, and
    /// automatic watering needs calibrated sensors. Safety guards apply to every tier.
    /// </summary>
    public static ModeCheck CheckMode(ActuatorKind kind, ControlMode requested, Tier tier, bool thermalCutoffDeclared, bool sensorsCalibrated)
    {
        var metric = MetricFor(kind);
        if (requested > TierPolicy.LimitsFor(tier).MaxModeFor(metric)) return ModeCheck.TierRequired;
        if (requested != ControlMode.Auto) return ModeCheck.Allowed;
        if (kind == ActuatorKind.Heater && !thermalCutoffDeclared) return ModeCheck.ThermalCutoffRequired;
        if (kind == ActuatorKind.Pump && !sensorsCalibrated) return ModeCheck.CalibrationRequired;
        return ModeCheck.Allowed;
    }
}