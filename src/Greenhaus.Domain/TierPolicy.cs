namespace Greenhaus.Domain;

public sealed record AutomationLevel(Metric Metric, ControlMode MaxMode);

public sealed record TierLimits(
    int? Pots,
    int HistoryDays,
    int AiQuestionsPerMonth,
    AiDataAccess AiDataAccess,
    int ZoneGridMax,
    IReadOnlyList<AutomationLevel> Automation)
{
    public ControlMode MaxModeFor(Metric metric) => Automation.First(a => a.Metric == metric).MaxMode;
}

/// <summary>
/// What each tier includes. Safety is never gated: limits, the kill switch and the audit log are the same for everyone.
/// The numbers are placeholders to tune, see the paywall balance in the product spec.
/// </summary>
public static class TierPolicy
{
    private static readonly Metric[] All = Enum.GetValues<Metric>();

    private static IReadOnlyList<AutomationLevel> Levels(params Metric[] auto) =>
        [.. All.Select(m => new AutomationLevel(m, auto.Contains(m) ? ControlMode.Auto : ControlMode.Notify))];

    public static readonly Tier[] Tiers = [Tier.Novice, Tier.Gardener, Tier.Druid];

    public static TierLimits LimitsFor(Tier tier) => tier switch
    {
        Tier.Novice => new TierLimits(2, 7, 10, AiDataAccess.None, 1, Levels(Metric.SoilMoisture, Metric.Light)),
        Tier.Gardener => new TierLimits(6, 30, 100, AiDataAccess.Current, 2,
            Levels(Metric.SoilMoisture, Metric.Light, Metric.Temperature, Metric.AirHumidity, Metric.Airflow)),
        _ => new TierLimits(null, 365, 500, AiDataAccess.History, 8,
            Levels(Metric.SoilMoisture, Metric.Light, Metric.Temperature, Metric.AirHumidity, Metric.Airflow)),
    };

    public static Tier Next(Tier tier) => tier == Tier.Novice ? Tier.Gardener : Tier.Druid;

    public static Tier LowestTierAllowingAuto(Metric metric) =>
        Tiers.First(t => LimitsFor(t).MaxModeFor(metric) == ControlMode.Auto);
}