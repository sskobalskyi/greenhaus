using System.Text.Json.Serialization;
using Greenhaus.Domain;

namespace Greenhaus.Api.Contracts;

// Wire types of greenhaus-api.yaml. Enums travel as snake_case strings, absent values are omitted, and the contract tests
// check every response against the OpenAPI document.

public class Problem
{
    public string? Type { get; set; }
    public required string Title { get; set; }
    public required int Status { get; set; }
    public string? Detail { get; set; }
    public required string Code { get; set; }
    public string? LimitType { get; set; }
    public Tier? RequiredTier { get; set; }
}

public class ProfileDto
{
    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public required string Email { get; set; }
    public required string Locale { get; set; }
    public Tier Tier { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ProfileUpdate
{
    public string? DisplayName { get; set; }
    public string? Locale { get; set; }
}

public class AutomationLevelDto
{
    public Metric Metric { get; set; }
    public ControlMode MaxMode { get; set; }
}

public class LimitsDto
{
    // Required by the contract, null means unlimited, so it is written even when null.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Pots { get; set; }
    public int HistoryDays { get; set; }
    public int AiQuestionsPerMonth { get; set; }
    public AiDataAccess AiDataAccess { get; set; }
    public int ZoneGridMax { get; set; }
    public required List<AutomationLevelDto> Automation { get; set; }

    public static LimitsDto From(TierLimits limits) => new()
    {
        Pots = limits.Pots,
        HistoryDays = limits.HistoryDays,
        AiQuestionsPerMonth = limits.AiQuestionsPerMonth,
        AiDataAccess = limits.AiDataAccess,
        ZoneGridMax = limits.ZoneGridMax,
        Automation = [.. limits.Automation.Select(a => new AutomationLevelDto { Metric = a.Metric, MaxMode = a.MaxMode })],
    };
}

public class UsageDto
{
    public int Pots { get; set; }
    public int AiQuestionsThisMonth { get; set; }
}

public class EntitlementSourceDto
{
    public required string Type { get; set; }
    public Tier Tier { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public Guid? OrderId { get; set; }
    public DateTimeOffset? GraceEndsAt { get; set; }
}

public class EntitlementsDto
{
    public Tier Tier { get; set; }
    public required LimitsDto Limits { get; set; }
    public required UsageDto Usage { get; set; }
    public required List<EntitlementSourceDto> Sources { get; set; }
}

public class TierLimitsDto
{
    public Tier Tier { get; set; }
    public required LimitsDto Limits { get; set; }
}

public class RangeDto
{
    public double Min { get; set; }
    public double Max { get; set; }
}

public class SpeciesDto
{
    public required string Id { get; set; }
    public required string CommonName { get; set; }
    public required string ScientificName { get; set; }
    public string? Category { get; set; }
    public required Dictionary<string, RangeDto> Preferred { get; set; }
    public int? FertilizerIntervalDays { get; set; }
    public string? Notes { get; set; }

    public static SpeciesDto From(Species s) => new()
    {
        Id = s.Id, CommonName = s.CommonName, ScientificName = s.ScientificName, Category = s.Category,
        FertilizerIntervalDays = s.FertilizerIntervalDays,
        Preferred = s.Preferred.ToDictionary(p => MetricNames.Key(p.Key), p => new RangeDto { Min = p.Value.Min, Max = p.Value.Max }),
    };
}

public class PotDto
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Location { get; set; }
    public PotKind Kind { get; set; }
    public ViewMode ViewMode { get; set; }
    public int? GridRows { get; set; }
    public int? GridCols { get; set; }
    public bool KillSwitch { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class PotCreate
{
    public string? Name { get; set; }
    public string? Location { get; set; }
    public PotKind? Kind { get; set; }
}

public class PotUpdate
{
    public string? Name { get; set; }
    public string? Location { get; set; }
}

public class PrimaryChannel
{
    public Metric Metric { get; set; }
    public required string ChannelId { get; set; }
}

public class ViewModeUpdate
{
    public ViewMode? Mode { get; set; }
    public int? GridRows { get; set; }
    public int? GridCols { get; set; }
    public List<PrimaryChannel>? PrimaryChannels { get; set; }
}

public class ZoneDto
{
    public Guid Id { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public string? Label { get; set; }
}

public class LabelUpdate
{
    public string? Label { get; set; }
}

public class PlantDto
{
    public Guid Id { get; set; }
    public required string SpeciesId { get; set; }
    public required string Nickname { get; set; }
    public Guid? ZoneId { get; set; }
    public DateOnly PlantedAt { get; set; }
    public string? Notes { get; set; }
}

public class PlantCreate
{
    public string? SpeciesId { get; set; }
    public string? Nickname { get; set; }
    public Guid? ZoneId { get; set; }
    public DateOnly? PlantedAt { get; set; }
    public string? Notes { get; set; }
}

public class PlantUpdate
{
    public string? Nickname { get; set; }
    public Guid? ZoneId { get; set; }
    public string? Notes { get; set; }
}

public class MetricRequirementDto
{
    public Metric Metric { get; set; }
    public bool Enabled { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Margin { get; set; }
}

public class RequirementsDto
{
    public List<MetricRequirementDto> Metrics { get; set; } = [];
}

public class RequirementConflictDto
{
    public Metric Metric { get; set; }
    public required List<Guid> Plants { get; set; }
}

public class RequirementsSuggestionDto
{
    public required RequirementsDto Requirements { get; set; }
    public required List<RequirementConflictDto> Conflicts { get; set; }
}

public class MetricReadingDto
{
    public Metric Metric { get; set; }
    public string? ChannelId { get; set; }
    public double? Value { get; set; }
    public string? Unit { get; set; }
    public ReadingState State { get; set; }
    public string? Direction { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class ZoneStatusDto
{
    public Guid ZoneId { get; set; }
    public ReadingState State { get; set; }
    public required List<MetricReadingDto> Metrics { get; set; }
}

public class ActiveAlertDto
{
    public required string Id { get; set; }
    public Metric Metric { get; set; }
    public Guid ZoneId { get; set; }
    public DateTimeOffset Since { get; set; }
    public string? Message { get; set; }
}

public class PotStatusDto
{
    public Guid PotId { get; set; }
    public ReadingState State { get; set; }
    public Guid? WorstZoneId { get; set; }
    public required List<ZoneStatusDto> Zones { get; set; }
    public List<ActiveAlertDto>? Alerts { get; set; }
}

public class LinkedDeviceDto
{
    public required string DeviceId { get; set; }
    public required string Model { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? Firmware { get; set; }
    public bool ThermalCutoffDeclared { get; set; }
}

public class SafetyLimitsDto
{
    public int MaxRunSeconds { get; set; }
    public int MinIntervalSeconds { get; set; }
    public int DailyCapSeconds { get; set; }
    public int? MaxFruitlessRuns { get; set; }

    public static SafetyLimitsDto From(SafetyLimits l) => new()
    {
        MaxRunSeconds = l.MaxRunSeconds, MinIntervalSeconds = l.MinIntervalSeconds, DailyCapSeconds = l.DailyCapSeconds, MaxFruitlessRuns = l.MaxFruitlessRuns,
    };

    public SafetyLimits ToDomain() => new(MaxRunSeconds, MinIntervalSeconds, DailyCapSeconds, MaxFruitlessRuns);
}

public class ActuatorDto
{
    public required string Id { get; set; }
    public ActuatorKind Kind { get; set; }
    public Metric Metric { get; set; }
    public ControlMode Mode { get; set; }
    public ActuatorState State { get; set; }
    public ControlMode? MaxModeForTier { get; set; }
    public required SafetyLimitsDto Limits { get; set; }
    public SafetyLimitsDto? HardLimits { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
}

public class ModeUpdate
{
    public ControlMode? Mode { get; set; }
}

public class RunRequest
{
    public int? DurationSeconds { get; set; }
}

public class KillSwitchUpdate
{
    public bool? Enabled { get; set; }
}

public class CommandReceiptDto
{
    public Guid CommandId { get; set; }
    public required string Status { get; set; }
}

public class AuditEntryDto
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public string? ActuatorId { get; set; }
    public required string Action { get; set; }
    public required string Reason { get; set; }
    public string? Outcome { get; set; }
}

public class CalibrationDto
{
    public double DryRaw { get; set; }
    public double WetRaw { get; set; }
    public DateTimeOffset? CalibratedAt { get; set; }
}

public class CareTaskCreate
{
    public string? Title { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public int? RepeatEveryDays { get; set; }
}

public class CareTaskDto
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public required string State { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Origin { get; set; }
}

public class ClaimChannelDto
{
    public required string ChannelId { get; set; }
    public Metric Metric { get; set; }
}

public class ClaimActuatorDto
{
    public required string Id { get; set; }
    public ActuatorKind Kind { get; set; }
}

public class ClaimPreviewDto
{
    public required string DeviceId { get; set; }
    public required string Model { get; set; }
    public bool Claimable { get; set; }
    public string? Reason { get; set; }
    public List<ClaimChannelDto>? Channels { get; set; }
    public List<ClaimActuatorDto>? Actuators { get; set; }
}

public class ClaimRequest
{
    public string? DeviceId { get; set; }
    public string? Token { get; set; }
    public Guid? PotId { get; set; }
}

public class ClaimDto
{
    public required string DeviceId { get; set; }
    public Guid PotId { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
}