using Greenhaus.Domain;

namespace Greenhaus.Api.Persistence;

public class UserProfileEntity
{
    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public required string Email { get; set; }
    public required string Locale { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The effective tier of a user as billing last said it. No row means the configured default tier.</summary>
public class EntitlementEntity
{
    public required string UserId { get; set; }
    public Tier Tier { get; set; }
    /// <summary>"subscription" or "purchase_grant", from the reason billing gave.</summary>
    public required string Source { get; set; }
    /// <summary>"active", "at_risk" or "past_due".</summary>
    public required string Status { get; set; }
    public Guid? OrderId { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class PotEntity
{
    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Location { get; set; }
    public PotKind Kind { get; set; }
    public ViewMode ViewMode { get; set; }
    public int? GridRows { get; set; }
    public int? GridCols { get; set; }
    public bool KillSwitch { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<ZoneEntity> Zones { get; set; } = [];
    public List<PlantEntity> Plants { get; set; } = [];
    public List<RequirementEntity> Requirements { get; set; } = [];
    public List<LinkedDeviceEntity> Devices { get; set; } = [];
    public List<ChannelEntity> Channels { get; set; } = [];
    public List<ActuatorEntity> Actuators { get; set; } = [];
    public List<AuditEntity> Audit { get; set; } = [];
    public List<CareTaskEntity> Tasks { get; set; } = [];
}

public class ZoneEntity
{
    public Guid Id { get; set; }
    public Guid PotId { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    /// <summary>A name the user chose. Null until they do, and the client shows a localized default.</summary>
    public string? Label { get; set; }
}

public class PlantEntity
{
    public Guid Id { get; set; }
    public Guid PotId { get; set; }
    public required string SpeciesId { get; set; }
    public required string Nickname { get; set; }
    public Guid? ZoneId { get; set; }
    public DateOnly PlantedAt { get; set; }
    public string? Notes { get; set; }
}

public class RequirementEntity
{
    public Guid PotId { get; set; }
    public Metric Metric { get; set; }
    public bool Enabled { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Margin { get; set; }
}

public class LinkedDeviceEntity
{
    public required string DeviceId { get; set; }
    public Guid PotId { get; set; }
    public required string OwnerId { get; set; }
    public required string Model { get; set; }
    public bool ThermalCutoffDeclared { get; set; }
    public Guid? OrderId { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

/// <summary>A sensor channel of a linked device and the zone it measures. Inactive channels keep their history but are not shown.</summary>
public class ChannelEntity
{
    public Guid PotId { get; set; }
    public required string DeviceId { get; set; }
    public required string ChannelId { get; set; }
    public Metric Metric { get; set; }
    public string? Unit { get; set; }
    public Guid ZoneId { get; set; }
    public bool Active { get; set; } = true;
    public double? DryRaw { get; set; }
    public double? WetRaw { get; set; }
    public DateTimeOffset? CalibratedAt { get; set; }
}

public class ActuatorEntity
{
    public Guid PotId { get; set; }
    public required string Id { get; set; }
    public required string DeviceId { get; set; }
    public ActuatorKind Kind { get; set; }
    public ControlMode Mode { get; set; }
    public int MaxRunSeconds { get; set; }
    public int MinIntervalSeconds { get; set; }
    public int DailyCapSeconds { get; set; }
    public int? MaxFruitlessRuns { get; set; }
    public int HardMaxRunSeconds { get; set; }
    public int HardMinIntervalSeconds { get; set; }
    public int HardDailyCapSeconds { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }

    // What the control loop needs to remember between cycles.
    /// <summary>Idle, or Blocked after a safety stop (dry run) until the user acts. Running is derived from <see cref="RunningUntil"/>.</summary>
    public ActuatorState State { get; set; }
    public string? BlockedReason { get; set; }
    public int LastRunSeconds { get; set; }
    public DateTimeOffset? RunningUntil { get; set; }
    /// <summary>Total run time on <see cref="CounterDay"/> (UTC), counted against the daily cap. Manual and automatic runs both count.</summary>
    public int RunSecondsToday { get; set; }
    public DateOnly? CounterDay { get; set; }
    /// <summary>Runs in a row after which the metric did not move.</summary>
    public int FruitlessRuns { get; set; }
    /// <summary>The reading the last automatic run started from, to see whether it helped.</summary>
    public double? LastRunStartValue { get; set; }
    /// <summary>Set while a notify-mode suggestion for the current episode has been sent, so it is sent once.</summary>
    public DateTimeOffset? SuggestedAt { get; set; }
    /// <summary>The last safety reason that was written down, so a lasting condition is said once, not every cycle.</summary>
    public string? LastNoteCode { get; set; }
}

public class AuditEntity
{
    public Guid Id { get; set; }
    public Guid PotId { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public string? ActuatorId { get; set; }
    public required string Action { get; set; }
    public required string Reason { get; set; }
    public string? Outcome { get; set; }
    /// <summary>The Pulse command behind a manual run, so its result can be written back.</summary>
    public Guid? CommandId { get; set; }
}

public class CareTaskEntity
{
    public Guid Id { get; set; }
    public Guid PotId { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public required string State { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Origin { get; set; }
    public int? RepeatEveryDays { get; set; }
}

/// <summary>An integration event waiting to be published, written in the same transaction as the change that caused it.</summary>
public class OutboxMessageEntity
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    /// <summary>W3C trace context of the request that caused the event, so a trace continues in the consumers.</summary>
    public string? Traceparent { get; set; }
}

/// <summary>A message already handled by a consumer. The pair is unique, so a redelivery is recognised and skipped.</summary>
public class InboxMessageEntity
{
    public Guid MessageId { get; set; }
    public required string Consumer { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Which order a device was allocated to, from device.allocated.v1, so the claim can name the order.</summary>
public class DeviceOrderEntity
{
    public required string DeviceId { get; set; }
    public Guid OrderId { get; set; }
    public required string CustomerId { get; set; }
    public DateTimeOffset AllocatedAt { get; set; }
}

/// <summary>Assistant questions answered for a user in a calendar month (UTC). One row per user and month.</summary>
public class AssistantUsageEntity
{
    public required string UserId { get; set; }
    /// <summary>The first day of the month.</summary>
    public DateOnly Month { get; set; }
    public int Count { get; set; }
    /// <summary>Places held by questions that are still running. They are not counted questions, and a place is given back when its question ends.</summary>
    public int Reserved { get; set; }
    /// <summary>When a place was last taken. Places older than the reservation lifetime belong to a process that died and are dropped.</summary>
    public DateTimeOffset? ReservedAt { get; set; }
}

/// <summary>Something that happened that a person should know. Only a kind and its facts are stored, clients write the text.</summary>
public class NotificationEntity
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Kind { get; set; }
    public Guid? PotId { get; set; }
    /// <summary>The facts for the text, a JSON object of strings.</summary>
    public string ParamsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    /// <summary>Names the thing a notification is about, so the same thing is told once. Null means no deduplication.</summary>
    public string? DedupeKey { get; set; }
}
