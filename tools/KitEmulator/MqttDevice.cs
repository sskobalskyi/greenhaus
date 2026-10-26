using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Greenhaus.KitEmulator;

public sealed record BrokerSettings(string Host, int Port);

public sealed record DeviceSpec(string DeviceId, string Sku, string? Credential);

/// <summary>
/// The messages of one controller over MQTT. QoS 1 messages said while the broker is away wait and go out when the connection is back,
/// readings (QoS 0) are dropped, as a controller with a small memory would do.
/// </summary>
public sealed class MqttSink : IMessageSink
{
    private const int Capacity = 500;

    private readonly Queue<(string Topic, string Payload, bool Retain)> _waiting = new();
    private IMqttClient? _client;

    public void Attach(IMqttClient client) => _client = client;

    public void Detach() => _client = null;

    public async Task PublishAsync(string topic, string payload, bool retain, int qos, CancellationToken ct = default)
    {
        if (_client is { IsConnected: true } client)
        {
            try { await client.PublishAsync(Message(topic, payload, retain, qos), ct); return; }
            catch (Exception) when (!ct.IsCancellationRequested) { /* the connection broke: treat as away */ }
        }
        if (qos < 1) return;
        lock (_waiting)
        {
            if (_waiting.Count >= Capacity) _waiting.Dequeue();
            _waiting.Enqueue((topic, payload, retain));
        }
    }

    /// <summary>Sends what was said while the connection was away, in order.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        while (_client is { IsConnected: true } client)
        {
            (string Topic, string Payload, bool Retain) next;
            lock (_waiting)
            {
                if (_waiting.Count == 0) return;
                next = _waiting.Peek();
            }
            await client.PublishAsync(Message(next.Topic, next.Payload, next.Retain, 1), ct);
            lock (_waiting) _waiting.Dequeue();
        }
    }

    private static MqttApplicationMessage Message(string topic, string payload, bool retain, int qos) =>
        new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain)
            .WithQualityOfServiceLevel(qos >= 1 ? MqttQualityOfServiceLevel.AtLeastOnce : MqttQualityOfServiceLevel.AtMostOnce).Build();
}

/// <summary>
/// Connects a controller to the broker as its device: user name the device id, password the credential, a will that says offline,
/// a persistent session so a command sent while it was away waits for it. Reconnects with a growing pause.
/// </summary>
public sealed class MqttDevice(KitController controller, MqttSink sink, DeviceSpec spec, BrokerSettings broker, Action<string> log)
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromMilliseconds(200);

    private MqttClientOptions Options()
    {
        var topics = controller.Topics;
        var will = Json.Write(new StatusMessage("offline", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")));
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(broker.Host, broker.Port)
            .WithClientId(spec.DeviceId)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithCleanSession(false)
            .WithSessionExpiryInterval(3600)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithWillTopic(topics.Status)
            .WithWillPayload(will)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrEmpty(spec.Credential)) builder.WithCredentials(spec.DeviceId, spec.Credential);
        return builder.Build();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var commands = Channel.CreateUnbounded<string>();
        var worker = Task.Run(() => HandleCommandsAsync(commands.Reader, ct), CancellationToken.None);
        var ticker = Task.Run(() => TickAsync(ct), CancellationToken.None);
        var pause = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested)
        {
            using var client = new MqttFactory().CreateMqttClient();
            var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.DisconnectedAsync += _ => { gone.TrySetResult(); return Task.CompletedTask; };
            // Commands are handled by a worker: answering one means publishing, which must not happen inside the receive callback.
            client.ApplicationMessageReceivedAsync += e => { commands.Writer.TryWrite(e.ApplicationMessage.ConvertPayloadToString()); return Task.CompletedTask; };
            try
            {
                await client.ConnectAsync(Options(), ct);
                sink.Attach(client);
                log($"connected to {broker.Host}:{broker.Port}");
                pause = TimeSpan.FromSeconds(1);
                await client.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(controller.Topics.Commands).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
                await controller.StartAsync(ct);
                await sink.FlushAsync(ct);
                await Task.WhenAny(gone.Task, Task.Delay(Timeout.Infinite, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e) { log($"broker not reachable ({e.GetType().Name}: {e.Message})"); }

            if (ct.IsCancellationRequested)
            {
                // A goodbye, so the broker does not need the will.
                try { await controller.StopAsync(CancellationToken.None); await client.DisconnectAsync(); } catch (Exception) { }
                sink.Detach();
                break;
            }
            sink.Detach();
            log($"connection lost, trying again in {pause.TotalSeconds:0} s");
            try { await Task.Delay(pause, ct); } catch (OperationCanceledException) { break; }
            pause = TimeSpan.FromSeconds(Math.Min(pause.TotalSeconds * 2, 30));
        }

        commands.Writer.TryComplete();
        await Task.WhenAll(worker, ticker);
    }

    private async Task HandleCommandsAsync(ChannelReader<string> reader, CancellationToken ct)
    {
        await foreach (var message in reader.ReadAllAsync(CancellationToken.None))
        {
            try { await controller.HandleCommandAsync(message, CancellationToken.None); }
            catch (Exception e) { log($"could not handle a command ({e.Message})"); }
            if (ct.IsCancellationRequested) break;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await controller.TickAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log($"tick failed ({e.Message})"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}