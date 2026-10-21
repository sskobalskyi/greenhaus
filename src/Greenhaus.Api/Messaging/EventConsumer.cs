using System.Diagnostics;
using System.Text.Json;
using Greenhaus.Api.Features;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Greenhaus.Api.Messaging;

/// <summary>
/// Consumes the events Greenhaus acts on. Each message is handled once: the handler's changes and the inbox row that records
/// the message id are saved in one transaction, so a redelivery finds the row and is skipped. A message that cannot be
/// handled is retried once and then goes to the dead-letter queue instead of blocking the others.
/// </summary>
public sealed class EventConsumer(IServiceScopeFactory scopes, IOptions<RabbitOptions> options, ILogger<EventConsumer> log) : BackgroundService
{
    public const string ConsumerName = "greenhaus-api";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await Rabbit.ConnectAsync(o, stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await Topology(channel, o, stoppingToken);
                await channel.BasicQosAsync(0, 10, false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => OnMessage(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(o.Queue, autoAck: false, consumer, stoppingToken);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception e)
            {
                log.LogWarning(e, "The event consumer lost the broker, retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    public static async Task Topology(IChannel channel, RabbitOptions o, CancellationToken ct)
    {
        await Rabbit.DeclareExchangeAsync(channel, o, ct);
        await channel.ExchangeDeclareAsync(Rabbit.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(o.Queue + ".dead", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(o.Queue + ".dead", Rabbit.DeadLetterExchange, "", cancellationToken: ct);
        await channel.QueueDeclareAsync(o.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = Rabbit.DeadLetterExchange }, cancellationToken: ct);
        foreach (var key in Rabbit.Consumed) await channel.QueueBindAsync(o.Queue, o.Exchange, key, cancellationToken: ct);
    }

    private async Task OnMessage(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        ActivityContext.TryParse(Rabbit.Text(delivery.BasicProperties.Headers, "traceparent"), null, out var parent);
        using var activity = GreenhausTelemetry.Source.StartActivity($"{delivery.RoutingKey} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", delivery.RoutingKey);

        try
        {
            var id = Guid.TryParse(Rabbit.Text(delivery.BasicProperties.Headers, "messageId") ?? delivery.BasicProperties.MessageId, out var parsed) ? parsed : (Guid?)null;
            if (id is null) throw new InvalidDataException("The message has no message id");
            activity?.SetTag("messaging.message.id", id.ToString());

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GreenhausDbContext>();
            if (await db.Inbox.AnyAsync(m => m.MessageId == id && m.Consumer == ConsumerName, ct))
            {
                GreenhausTelemetry.InboxDuplicates.Add(1);
            }
            else
            {
                using var payload = JsonDocument.Parse(delivery.Body);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await EventHandlers.HandleAsync(delivery.RoutingKey, payload.RootElement, scope.ServiceProvider, ct);
                db.Inbox.Add(new InboxMessageEntity { MessageId = id.Value, Consumer = ConsumerName, ProcessedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                GreenhausTelemetry.InboxProcessed.Add(1, new KeyValuePair<string, object?>("type", delivery.RoutingKey));
            }
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A duplicate that raced past the check collides on the inbox key: it was handled, so acknowledge it.
            if (e is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } } && e.ToString().Contains("inbox"))
            {
                GreenhausTelemetry.InboxDuplicates.Add(1);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                return;
            }
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            var firstFailure = !delivery.Redelivered && e is not (InvalidDataException or JsonException or KeyNotFoundException);
            if (!firstFailure) GreenhausTelemetry.InboxDeadLettered.Add(1, new KeyValuePair<string, object?>("type", delivery.RoutingKey));
            log.LogWarning(e, "Could not handle {Key}, {Outcome}", delivery.RoutingKey, firstFailure ? "retrying once" : "dead-lettering");
            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: firstFailure, ct);
        }
    }
}

public static class EventHandlers
{
    public static Task HandleAsync(string routingKey, JsonElement payload, IServiceProvider services, CancellationToken ct) => routingKey switch
    {
        "entitlement.changed.v1" => EntitlementChanged(payload, services, ct),
        "device.allocated.v1" => DeviceAllocated(payload, services, ct),
        "device.retired.v1" => DeviceRetired(payload, services, ct),
        "command.completed.v1" => CommandCompleted(payload, services, ct),
        _ => throw new InvalidDataException($"No handler for {routingKey}"),
    };

    private static async Task EntitlementChanged(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var tier = Enum.Parse<Tier>(p.GetProperty("tier").GetString()!, ignoreCase: true);
        var reason = p.GetProperty("reason").GetString()!;
        var source = reason.StartsWith("grant_", StringComparison.Ordinal) ? "purchase_grant" : "subscription";
        var status = reason switch { "grant_at_risk" => "at_risk", "payment_failed" => "past_due", _ => "active" };
        var validUntil = p.TryGetProperty("validUntil", out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : (DateTimeOffset?)null;
        await services.GetRequiredService<EntitlementService>()
            .SetAsync(p.GetProperty("userId").GetString()!, tier, source, status, validUntil, p.GetProperty("at").GetDateTimeOffset(), ct);
    }

    private static async Task DeviceAllocated(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<GreenhausDbContext>();
        var deviceId = p.GetProperty("deviceId").GetString()!;
        var row = await db.DeviceOrders.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (row is null) db.DeviceOrders.Add(row = new DeviceOrderEntity { DeviceId = deviceId, CustomerId = "" });
        row.OrderId = p.GetProperty("orderId").GetGuid();
        row.CustomerId = p.GetProperty("customerId").GetString()!;
        row.AllocatedAt = services.GetRequiredService<TimeProvider>().GetUtcNow();
    }

    private static async Task DeviceRetired(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<GreenhausDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();
        var deviceId = p.GetProperty("deviceId").GetString()!;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null) return;

        // The pot keeps its history, but the controller and what hangs on it are gone.
        await db.Actuators.Where(a => a.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await db.Channels.Where(c => c.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        db.Devices.Remove(device);
        db.AddAudit(device.PotId, "automation", "device_retired", $"Controller {deviceId} was retired", null, null, clock);
    }

    private static async Task CommandCompleted(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<GreenhausDbContext>();
        var commandId = p.GetProperty("commandId").GetGuid();
        var outcome = p.GetProperty("status").GetString() switch { "rejected" => "refused", var s => s };
        await db.Audit.Where(a => a.CommandId == commandId && a.Outcome == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.Outcome, outcome), ct);
    }
}