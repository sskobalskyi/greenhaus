namespace Greenhaus.MockServer;

// Enums serialize as snake_case strings, matching the OpenAPI contract.
public enum Tier { Novice, Gardener, Druid }
public enum Metric { Temperature, AirHumidity, SoilMoisture, Ph, Light, Airflow }
public enum ControlMode { Off, Notify, Auto }
public enum ViewMode { Simple, Zones }
public enum ReadingState { Ok, Attention, Offline, Unknown }
public enum ActuatorKind { Pump, Heater, Lamp, Fan, Humidifier }
public enum ActuatorState { Idle, Running, Blocked, Fault }
public enum PotKind { Pot, Balcony, Bed }

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

public class Profile
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

public class AutomationLevel
{
    public Metric Metric { get; set; }
    public ControlMode MaxMode { get; set; }
}

public class Limits
{
    // Required by the contract, null means unlimited, so it must be written even when null.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public int? Pots { get; set; }
    public int HistoryDays { get; set; }
    public int AiQuestionsPerMonth { get; set; }
    public required string AiDataAccess { get; set; }
    public int ZoneGridMax { get; set; }
    public required List<AutomationLevel> Automation { get; set; }
}

public class TierLimits
{
    public Tier Tier { get; set; }
    public required Limits Limits { get; set; }
}

public class Usage
{
    public int Pots { get; set; }
    public int AiQuestionsThisMonth { get; set; }
}

public class EntitlementSource
{
    public required string Type { get; set; }
    public Tier Tier { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public Guid? OrderId { get; set; }
    public DateTimeOffset? GraceEndsAt { get; set; }
}

public class Entitlements
{
    public Tier Tier { get; set; }
    public required Limits Limits { get; set; }
    public required Usage Usage { get; set; }
    public required List<EntitlementSource> Sources { get; set; }
}

public class Range
{
    public double Min { get; set; }
    public double Max { get; set; }
}

public class Species
{
    public required string Id { get; set; }
    public required string CommonName { get; set; }
    public required string ScientificName { get; set; }
    public string? Category { get; set; }
    public required Dictionary<string, Range> Preferred { get; set; }
    public int? FertilizerIntervalDays { get; set; }
    public string? Notes { get; set; }
}

public class SpeciesFile
{
    public List<Species> Species { get; set; } = [];
}

public class Pot
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

public class Zone
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

public class Plant
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

public class MetricRequirement
{
    public Metric Metric { get; set; }
    public bool Enabled { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Margin { get; set; }
}

public class Requirements
{
    public List<MetricRequirement> Metrics { get; set; } = [];
}

public class RequirementConflict
{
    public Metric Metric { get; set; }
    public required List<Guid> Plants { get; set; }
}

public class RequirementsSuggestion
{
    public required Requirements Requirements { get; set; }
    public required List<RequirementConflict> Conflicts { get; set; }
}

public class MetricReading
{
    public Metric Metric { get; set; }
    public string? ChannelId { get; set; }
    public double? Value { get; set; }
    public string? Unit { get; set; }
    public ReadingState State { get; set; }
    public string? Direction { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class ZoneStatus
{
    public Guid ZoneId { get; set; }
    public ReadingState State { get; set; }
    public required List<MetricReading> Metrics { get; set; }
}

public class ActiveAlert
{
    public required string Id { get; set; }
    public Metric Metric { get; set; }
    public Guid ZoneId { get; set; }
    public DateTimeOffset Since { get; set; }
    public string? Message { get; set; }
}

public class PotStatus
{
    public Guid PotId { get; set; }
    public ReadingState State { get; set; }
    public Guid? WorstZoneId { get; set; }
    public required List<ZoneStatus> Zones { get; set; }
    public List<ActiveAlert>? Alerts { get; set; }
}

public class LinkedDevice
{
    public required string DeviceId { get; set; }
    public required string Model { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? Firmware { get; set; }
    public bool ThermalCutoffDeclared { get; set; }
}

public class SafetyLimits
{
    public int MaxRunSeconds { get; set; }
    public int MinIntervalSeconds { get; set; }
    public int DailyCapSeconds { get; set; }
    public int? MaxFruitlessRuns { get; set; }
}

public class Actuator
{
    public required string Id { get; set; }
    public ActuatorKind Kind { get; set; }
    public Metric Metric { get; set; }
    public ControlMode Mode { get; set; }
    public ActuatorState State { get; set; }
    public ControlMode? MaxModeForTier { get; set; }
    public required SafetyLimits Limits { get; set; }
    public SafetyLimits? HardLimits { get; set; }
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

public class CommandReceipt
{
    public Guid CommandId { get; set; }
    public required string Status { get; set; }
}

public class AuditEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public string? ActuatorId { get; set; }
    public required string Action { get; set; }
    public required string Reason { get; set; }
    public string? Outcome { get; set; }
}

public class Calibration
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

public class CareTask
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public required string State { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Origin { get; set; }
}

public class ClaimChannel
{
    public required string ChannelId { get; set; }
    public Metric Metric { get; set; }
}

public class ClaimActuator
{
    public required string Id { get; set; }
    public ActuatorKind Kind { get; set; }
}

public class ClaimPreview
{
    public required string DeviceId { get; set; }
    public required string Model { get; set; }
    public bool Claimable { get; set; }
    public string? Reason { get; set; }
    public List<ClaimChannel>? Channels { get; set; }
    public List<ClaimActuator>? Actuators { get; set; }
}

public class ClaimRequest
{
    public string? DeviceId { get; set; }
    public string? Token { get; set; }
    public Guid? PotId { get; set; }
}

public class Claim
{
    public required string DeviceId { get; set; }
    public Guid PotId { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
}
