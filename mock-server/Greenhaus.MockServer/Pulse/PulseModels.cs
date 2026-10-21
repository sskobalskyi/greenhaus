namespace Greenhaus.MockServer.Pulse;

// Input types use strings for enumerations so an unknown value is reported as a validation problem
// together with all the other problems, instead of failing deserialization with a bare 400.

public class Calibration
{
    public double? DryRaw { get; set; }
    public double? WetRaw { get; set; }
    public DateTimeOffset? CalibratedAt { get; set; }
}

public class PChannel
{
    public string? Id { get; set; }
    public string? Metric { get; set; }
    public string? Label { get; set; }
    public string? Unit { get; set; }
    public Calibration? Calibration { get; set; }
}

public class HardLimits
{
    public int? MaxRunSeconds { get; set; }
    public int? MinIntervalSeconds { get; set; }
    public int? DailyCapSeconds { get; set; }
}

public class ActuatorCapability
{
    public string? Id { get; set; }
    public string? Kind { get; set; }
    public HardLimits? HardLimits { get; set; }
    public bool? ThermalCutoff { get; set; }
}

public class Capabilities
{
    public List<PChannel>? Channels { get; set; }
    public List<ActuatorCapability>? Actuators { get; set; }
}

public class DeviceRegistration
{
    public string? Id { get; set; }
    public string? Model { get; set; }
    public Capabilities? Capabilities { get; set; }
    public string? CredentialHash { get; set; }
    public string? ClaimTokenHash { get; set; }
    public DateTimeOffset? ClaimTokenExpiresAt { get; set; }
}

public class ClaimRequest
{
    public string? Token { get; set; }
    public string? OwnerId { get; set; }
}

public class Device
{
    public required string Id { get; set; }
    public required string Model { get; set; }
    public required string State { get; set; }
    public string? OwnerId { get; set; }
    public string? Connection { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? Firmware { get; set; }
    public required Capabilities Capabilities { get; set; }
}

public class Rule
{
    public string? ChannelId { get; set; }
    public string? Kind { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Margin { get; set; }
    public int? TimeoutSeconds { get; set; }
}

public class CommandRequest
{
    public Guid? CommandId { get; set; }
    public string? Type { get; set; }
    public string? ActuatorId { get; set; }
    public int? DurationSeconds { get; set; }
    public bool? Enabled { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class Command
{
    public Guid CommandId { get; set; }
    public required string DeviceId { get; set; }
    public required string Type { get; set; }
    public string? ActuatorId { get; set; }
    public required string Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public class Point
{
    public DateTimeOffset Ts { get; set; }
    public double Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
}

/// <summary>Mock-only: simulates the first reading, or silence, of a device.</summary>
public class SimulateRequest
{
    public string? Connection { get; set; }
}
