using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Greenhaus.KitEmulator;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Greenhaus.KitEmulator.Tests;

/// <summary>
/// The emulator against a real broker (EMQX, the one Pulse uses): it connects as its device, says what it is, reports, and executes
/// and answers commands that the test publishes the way Pulse's bridge will. Every message in both directions is checked against
/// contracts/asyncapi/device-mqtt.yaml.
/// </summary>
public sealed class BrokerTests : IAsyncLifetime
{
    private const string DeviceId = "ghx-broker0001";

    private readonly IContainer _broker = new ContainerBuilder()
        .WithImage("emqx/emqx:5.8.6")
        .WithPortBinding(1883, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(1883))
        .Build();

    private readonly List<(string Topic, string Payload)> _heard = [];
    private IMqttClient _observer = null!;
    private CancellationTokenSource _stop = new();
    private Task _device = Task.CompletedTask;
    private readonly Topics _topics = new(DeviceId);

    public async Task InitializeAsync()
    {
        await _broker.StartAsync();
        var port = _broker.GetMappedPublicPort(1883);

        // The observer plays the platform: it listens to everything of the device and sends commands.
        _observer = new MqttFactory().CreateMqttClient();
        _observer.ApplicationMessageReceivedAsync += e =>
        {
            lock (_heard) _heard.Add((e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString()));
            return Task.CompletedTask;
        };
        await ConnectWithRetryAsync(port);
        await _observer.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic($"greenhaus/v1/devices/{DeviceId}/#").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());

        var kit = Repo.Kits["GH-POT-KIT-01"];
        var physics = new PlantPhysics(new PhysicsOptions { Noise = 0, InitialSoilMoisture = 30 });
        var sink = new MqttSink();
        var controller = new KitController(kit, DeviceId, physics, TimeProvider.System, sink, new ControllerOptions { TelemetryInterval = TimeSpan.FromSeconds(1) });
        var device = new MqttDevice(controller, sink, new DeviceSpec(DeviceId, "GH-POT-KIT-01", null), new BrokerSettings("localhost", port), _ => { });
        _device = Task.Run(() => device.RunAsync(_stop.Token));
    }

    private async Task ConnectWithRetryAsync(int port)
    {
        var options = new MqttClientOptionsBuilder().WithTcpServer("localhost", port).WithClientId("platform").WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500).Build();
        for (var attempt = 0; ; attempt++)
        {
            try { await _observer.ConnectAsync(options); return; }
            catch (Exception) when (attempt < 60) { await Task.Delay(1000); }
        }
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        try { await _device.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        await _observer.DisconnectAsync();
        await _broker.DisposeAsync();
    }

    private List<(string Topic, string Payload)> Heard(string topic) { lock (_heard) return _heard.Where(h => h.Topic == topic).ToList(); }

    private async Task<T> Eventually<T>(Func<T?> probe, string what, int seconds = 40) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } found) return found;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}");
    }

    private async Task<double> EventuallyValue(Func<double?> probe, string what, int seconds = 40)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } found) return found;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}");
    }

    private async Task PublishCommandAsync(object command)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(command);
        Repo.Verify("command", json);
        await _observer.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(_topics.Commands).WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
    }

    private static object Run(string actuator, int seconds, Guid id) => new
    {
        commandId = id, type = "run_actuator", actuatorId = actuator, durationSeconds = seconds, expiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToString("O"),
    };

    private List<(string Status, string? Reason)> Acks(Guid id) =>
        [.. Heard(_topics.CommandAck).Select(h => JsonNode.Parse(h.Payload)!).Where(a => a["commandId"]!.GetValue<Guid>() == id)
            .Select(a => (a["status"]!.GetValue<string>(), a["reason"]?.GetValue<string>()))];

    private double? LastReading(string channel) =>
        Heard(_topics.Telemetry(channel)).LastOrDefault() is { Payload: { } p } ? JsonNode.Parse(p)!["value"]!.GetValue<double>() : null;

    [Fact]
    public async Task It_says_what_it_is_reports_runs_a_command_refuses_the_one_beyond_its_limit_and_says_goodbye()
    {
        // 1. It connects and introduces itself.
        var manifest = await Eventually(() => Heard(_topics.Manifest).FirstOrDefault().Payload, "the manifest");
        Repo.Verify("manifest", manifest);
        var status = await Eventually(() => Heard(_topics.Status).FirstOrDefault().Payload, "the status");
        Repo.Verify("status", status);
        Assert.Equal("online", JsonNode.Parse(status)!["status"]!.GetValue<string>());
        var state = await Eventually(() => Heard(_topics.State).FirstOrDefault().Payload, "the state");
        Repo.Verify("state", state);

        // 2. It reports every channel, and the readings are as the contract says.
        var soilBefore = await EventuallyValue(() => LastReading("soil"), "a soil reading");
        await EventuallyValue(() => LastReading("tank"), "a tank reading");
        Assert.All(Heard(_topics.Telemetry("soil")), h => Repo.Verify("reading", h.Payload));
        Assert.InRange(soilBefore, 29, 31);

        // 3. A run command from the platform: accepted, completed, and the soil gets wetter in the telemetry.
        var run = Guid.NewGuid();
        await PublishCommandAsync(Run("pump", 5, run));
        await Eventually(() => Acks(run).Any(a => a.Status == "completed") ? Acks(run) : null, "the run to complete");
        Assert.Equal([("accepted", (string?)null), ("completed", null)], Acks(run));
        Assert.All(Heard(_topics.CommandAck), h => Repo.Verify("ack", h.Payload));
        var wetter = await EventuallyValue(() => LastReading("soil") is { } soil && soil > soilBefore + 2 ? soil : null, "the soil to get wetter");
        Assert.True(wetter > soilBefore + 2);
        Assert.Contains(Heard(_topics.State).Select(h => h.Payload), p => p.Contains("\"running\""));

        // 4. The same command again (QoS 1 may deliver twice) is answered again and not run twice: the tank was used once.
        await PublishCommandAsync(Run("pump", 5, run));
        await Eventually(() => Acks(run).Count >= 3 ? Acks(run) : null, "the repeat to be answered");
        Assert.Equal(("completed", (string?)null), Acks(run)[^1]);
        var tank = await EventuallyValue(() => LastReading("tank"), "the tank level");
        Assert.InRange(tank, 97, 98.5);

        // 5. A command beyond the hard limit is refused by the controller whatever the platform asks.
        var tooLong = Guid.NewGuid();
        await PublishCommandAsync(Run("pump", 61, tooLong));
        await Eventually(() => Acks(tooLong).Count > 0 ? Acks(tooLong) : null, "the refusal");
        Assert.Equal([("rejected", "exceeds_hard_limit")], Acks(tooLong));

        // 6. It says goodbye when it is stopped, so the broker does not have to publish the will.
        _stop.Cancel();
        await _device.WaitAsync(TimeSpan.FromSeconds(10));
        var goodbye = await Eventually(() => Heard(_topics.Status).Select(h => h.Payload).LastOrDefault(p => p.Contains("offline")), "the offline status");
        Repo.Verify("status", goodbye);
    }
}