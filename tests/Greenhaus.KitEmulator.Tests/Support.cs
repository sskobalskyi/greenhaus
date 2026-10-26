using System.Text.Json.Nodes;
using Greenhaus.ContractTesting;
using Greenhaus.KitEmulator;
using Microsoft.Extensions.Time.Testing;

namespace Greenhaus.KitEmulator.Tests;

public sealed record Sent(string Topic, string Payload, bool Retain, int Qos)
{
    public JsonNode Json => JsonNode.Parse(Payload)!;
}

public sealed class RecordingSink : IMessageSink
{
    public List<Sent> Messages { get; } = [];

    public Task PublishAsync(string topic, string payload, bool retain, int qos, CancellationToken ct = default)
    {
        lock (Messages) Messages.Add(new Sent(topic, payload, retain, qos));
        return Task.CompletedTask;
    }

    public List<Sent> On(string topic) { lock (Messages) return Messages.Where(m => m.Topic == topic).ToList(); }
}

/// <summary>The repository's files and the contract every message of the emulator is checked against.</summary>
public static class Repo
{
    public static readonly string Root = FindRoot();

    private static readonly Lazy<AsyncApiContract> DeviceContract = new(() => new AsyncApiContract(Path.Combine(Root, "contracts", "asyncapi", "device-mqtt.yaml")));

    public static IReadOnlyDictionary<string, KitDescription> Kits { get; } = Greenhaus.KitEmulator.Kits.Load(Path.Combine(Root, "data", "kit-capabilities.json"));

    /// <summary>Fails the test when a message does not match the device topics contract. The message names are manifest, reading, state, status, command and ack.</summary>
    public static void Verify(string message, string payload) => DeviceContract.Value.VerifyPayload(message, payload);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "asyncapi", "device-mqtt.yaml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}

/// <summary>One kit on a fake clock, with exact readings and a recording transport.</summary>
public sealed class Rig
{
    public const string DeviceId = "ghx-test0001";

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    public RecordingSink Sink { get; } = new();
    public PlantPhysics Physics { get; }
    public KitController Controller { get; }
    public Topics Topics => Controller.Topics;

    public Rig(string sku = "GH-POT-KIT-01", Action<PhysicsOptions>? physics = null, Action<ControllerOptions>? controller = null)
    {
        var options = new PhysicsOptions { Noise = 0 };
        physics?.Invoke(options);
        Physics = new PlantPhysics(options);
        var controllerOptions = new ControllerOptions();
        controller?.Invoke(controllerOptions);
        Controller = new KitController(Repo.Kits[sku], DeviceId, Physics, Time, Sink, controllerOptions);
    }

    /// <summary>Lets time pass in steps, as a running controller would see it.</summary>
    public async Task AdvanceAsync(double seconds, double stepSeconds = 1)
    {
        for (var left = seconds; left > 0; left -= stepSeconds)
        {
            Time.Advance(TimeSpan.FromSeconds(Math.Min(stepSeconds, left)));
            await Controller.TickAsync();
        }
    }

    public Task SendAsync(object command) => Controller.HandleCommandAsync(System.Text.Json.JsonSerializer.Serialize(command));

    public object Run(string actuator, int seconds, Guid? id = null, TimeSpan? validFor = null) => new
    {
        commandId = id ?? Guid.NewGuid(), type = "run_actuator", actuatorId = actuator, durationSeconds = seconds,
        expiresAt = (Time.GetUtcNow() + (validFor ?? TimeSpan.FromMinutes(2))).ToString("O"),
    };

    public object KillSwitch(bool enabled, Guid? id = null) => new
    {
        commandId = id ?? Guid.NewGuid(), type = "set_kill_switch", enabled, expiresAt = (Time.GetUtcNow() + TimeSpan.FromMinutes(2)).ToString("O"),
    };

    public object Stop(string actuator) => new
    {
        commandId = Guid.NewGuid(), type = "stop_actuator", actuatorId = actuator, expiresAt = (Time.GetUtcNow() + TimeSpan.FromMinutes(2)).ToString("O"),
    };

    /// <summary>The statuses of the acks for a command, in the order they were sent.</summary>
    public List<(string Status, string? Reason)> Acks(Guid commandId) =>
        [.. Sink.On(Topics.CommandAck).Select(m => m.Json).Where(a => a["commandId"]!.GetValue<Guid>() == commandId)
            .Select(a => (a["status"]!.GetValue<string>(), a["reason"]?.GetValue<string>()))];

    public JsonNode LastState() => Sink.On(Topics.State).Last().Json;

    public string ActuatorState(string actuator) =>
        LastState()["actuators"]!.AsArray().First(a => a!["id"]!.GetValue<string>() == actuator)!["state"]!.GetValue<string>();

    public string? BlockedReason(string actuator) =>
        LastState()["actuators"]!.AsArray().First(a => a!["id"]!.GetValue<string>() == actuator)!["blockedReason"]?.GetValue<string>();
}