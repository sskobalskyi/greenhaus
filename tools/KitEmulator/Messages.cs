using System.Text.Json;
using System.Text.Json.Serialization;

namespace Greenhaus.KitEmulator;

/// <summary>The topics of contracts/asyncapi/device-mqtt.yaml for one controller.</summary>
public sealed class Topics(string deviceId)
{
    private string Root => $"greenhaus/v1/devices/{deviceId}";

    public string Manifest => $"{Root}/manifest";
    public string Telemetry(string channelId) => $"{Root}/telemetry/{channelId}";
    public string State => $"{Root}/state";
    public string Status => $"{Root}/status";
    public string Commands => $"{Root}/commands";
    public string CommandAck => $"{Root}/commands/ack";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public sealed record ManifestChannel(string Id, string Metric, string? Unit);

public sealed record ManifestActuator(string Id, string Kind, bool? ThermalCutoff, HardLimits HardLimits);

public sealed record Manifest(string Model, string Firmware, IReadOnlyList<ManifestChannel> Channels, IReadOnlyList<ManifestActuator> Actuators);

public sealed record Reading(string Ts, double Value, long Seq);

public sealed record ActuatorState(string Id, string State, string? BlockedReason, int RunSecondsToday);

public sealed record State(string Ts, bool KillSwitch, IReadOnlyList<ActuatorState> Actuators);

public sealed record StatusMessage(string Status, string Ts);

/// <summary>A command as the bridge sends it. Anything the device cannot read is dropped, it never throws into the connection.</summary>
public sealed class Command
{
    public Guid CommandId { get; set; }
    public string Type { get; set; } = "";
    public string? ActuatorId { get; set; }
    public int? DurationSeconds { get; set; }
    public bool? Enabled { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public static Command? TryParse(string json)
    {
        try
        {
            var command = JsonSerializer.Deserialize<Command>(json, Json.Options);
            return command is null || command.CommandId == Guid.Empty || string.IsNullOrEmpty(command.Type) ? null : command;
        }
        catch (JsonException) { return null; }
    }
}

public sealed record Ack(Guid CommandId, string Status, string? Reason, string Ts);
