using System.Diagnostics;
using System.Text.Json;
using Greenhaus.Fulfilment.Domain;
using Greenhaus.Fulfilment.Infrastructure;
using Greenhaus.Fulfilment.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Greenhaus.Fulfilment.Messaging;

/// <summary>
/// Consumes the order events fulfilment acts on. Each message is handled once: the handler's changes and the inbox row that records
/// the message id are saved in one transaction, so a redelivery finds the row and is skipped. A message that cannot be handled is
/// retried once and then goes to the dead-letter queue instead of blocking the others.
/// </summary>
public sealed class EventConsumer(IServiceScopeFactory scopes, IOptions<RabbitOptions> options, ILogger<EventConsumer> log) : BackgroundService
{
    public const string ConsumerName = "greenhaus-fulfilment";

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
        var dlx = Rabbit.DeadLetterExchange(o);
        await Rabbit.DeclareExchangeAsync(channel, o, ct);
        await channel.ExchangeDeclareAsync(dlx, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(o.Queue + ".dead", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(o.Queue + ".dead", dlx, "", cancellationToken: ct);
        await channel.QueueDeclareAsync(o.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = dlx }, cancellationToken: ct);
        foreach (var key in Rabbit.Consumed) await channel.QueueBindAsync(o.Queue, o.Exchange, key, cancellationToken: ct);
    }

    private async Task OnMessage(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        ActivityContext.TryParse(Rabbit.Text(delivery.BasicProperties.Headers, "traceparent"), null, out var parent);
        using var activity = FulfilmentTelemetry.Source.StartActivity($"{delivery.RoutingKey} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", delivery.RoutingKey);

        try
        {
            var id = Guid.TryParse(Rabbit.Text(delivery.BasicProperties.Headers, "messageId") ?? delivery.BasicProperties.MessageId, out var parsed) ? parsed : (Guid?)null;
            if (id is null) throw new InvalidDataException("The message has no message id");
            activity?.SetTag("messaging.message.id", id.ToString());

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FulfilmentDbContext>();
            if (await db.Inbox.AnyAsync(m => m.MessageId == id && m.Consumer == ConsumerName, ct))
            {
                FulfilmentTelemetry.InboxDuplicates.Add(1);
            }
            else
            {
                using var payload = JsonDocument.Parse(delivery.Body);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await EventHandlers.HandleAsync(delivery.RoutingKey, payload.RootElement, scope.ServiceProvider, ct);
                db.Inbox.Add(new InboxMessageEntity { MessageId = id.Value, Consumer = ConsumerName, ProcessedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                FulfilmentTelemetry.InboxProcessed.Add(1, new KeyValuePair<string, object?>("type", delivery.RoutingKey));
            }
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A duplicate that raced past the check collides on the inbox key: it was handled, so acknowledge it.
            if (e is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } } && e.ToString().Contains("inbox"))
            {
                FulfilmentTelemetry.InboxDuplicates.Add(1);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                return;
            }
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            var firstFailure = !delivery.Redelivered && e is not (InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException);
            if (!firstFailure) FulfilmentTelemetry.InboxDeadLettered.Add(1, new KeyValuePair<string, object?>("type", delivery.RoutingKey));
            log.LogWarning(e, "Could not handle {Key}, {Outcome}", delivery.RoutingKey, firstFailure ? "retrying once" : "dead-lettering");
            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: firstFailure, ct);
        }
    }
}

public static class EventHandlers
{
    public static Task HandleAsync(string routingKey, JsonElement payload, IServiceProvider services, CancellationToken ct) => routingKey switch
    {
        "order.confirmed.v1" => OrderConfirmed(payload, services, ct),
        "order.shipped.v1" => OrderShipped(payload, services, ct),
        "order.returned.v1" or "order.refunded.v1" => OrderCancelled(payload, services, ct),
        _ => throw new InvalidDataException($"No handler for {routingKey}"),
    };

    /// <summary>Allocates a device for every kit unit on the order and announces each. Lines for other products are not ours.</summary>
    private static async Task OrderConfirmed(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<FulfilmentDbContext>();
        var kits = services.GetRequiredService<KitCatalog>();
        var events = services.GetRequiredService<EventPublisher>();
        var clock = services.GetRequiredService<TimeProvider>();
        var options = services.GetRequiredService<IOptions<FulfilmentOptions>>().Value;
        var secrets = services.GetRequiredService<SecretBox>();

        var orderId = p.GetProperty("orderId").GetGuid();
        var customerId = p.GetProperty("customerId").GetString()!;
        var now = clock.GetUtcNow();
        var existing = (await db.Allocations.AsNoTracking().Where(a => a.OrderId == orderId).Select(a => new { a.Sku, a.Index }).ToListAsync(ct))
            .Select(a => (a.Sku, a.Index)).ToHashSet();

        // A line of the same SKU may appear twice in one order, so the index counts across all its lines.
        var next = new Dictionary<string, int>();
        foreach (var line in p.GetProperty("lines").EnumerateArray())
        {
            var sku = line.GetProperty("sku").GetString()!;
            if (kits.Find(sku) is not { } kit) continue;

            for (var n = line.GetProperty("quantity").GetInt32(); n > 0; n--)
            {
                var index = next.GetValueOrDefault(sku);
                next[sku] = index + 1;
                if (existing.Contains((sku, index))) continue;

                var deviceId = Allocator.NewDeviceId();
                var token = Allocator.NewSecret();
                var credential = Allocator.NewSecret();
                var id = Guid.NewGuid();
                var allocation = new AllocationEntity
                {
                    Id = id, OrderId = orderId, CustomerId = customerId, Sku = sku, Index = index, DeviceId = deviceId, Model = kit.Model,
                    // Sealed for the database: the plaintext exists in memory here and in the packing bundle, nowhere else.
                    State = AllocationState.Allocated, AllocatedAt = now, ClaimToken = secrets.Seal(token, SecretBox.ClaimTokenField, id, deviceId),
                    Credential = secrets.Seal(credential, SecretBox.CredentialField, id, deviceId),
                    ClaimTokenHash = Allocator.ClaimTokenHash(token), ClaimTokenExpiresAt = now.AddDays(options.TokenLifetimeDays),
                    CredentialHash = Allocator.CredentialHash(credential), Capabilities = kit.Capabilities.ToJsonString(),
                };
                db.Allocations.Add(allocation);
                // Only hashes and what the device can do travel. The token and the password stay here until the device is packed.
                events.Stage("device.allocated.v1", new
                {
                    deviceId, orderId, customerId, sku, model = kit.Model,
                    claimTokenHash = allocation.ClaimTokenHash, claimTokenExpiresAt = allocation.ClaimTokenExpiresAt,
                    credentialHash = allocation.CredentialHash, capabilities = kit.Capabilities,
                });
                FulfilmentTelemetry.Allocated.Add(1, new KeyValuePair<string, object?>("sku", sku));
            }
        }
    }

    /// <summary>The order left the warehouse. Pulse is told, by the worker, that its devices can be claimed.</summary>
    private static async Task OrderShipped(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<FulfilmentDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var orderId = p.GetProperty("orderId").GetGuid();
        foreach (var a in await db.Allocations.Where(a => a.OrderId == orderId && a.State == AllocationState.Allocated).ToListAsync(ct))
        {
            a.State = AllocationState.ShipRequested;
            a.Intent = "ship";
            a.NextAttemptAt = now;
        }
    }

    /// <summary>
    /// A return or a refund. Devices that have not shipped yet never will, so Pulse retires them. Devices that already shipped are
    /// with the buyer: what happens to returned goods is Orderflow's concern, and these are left alone.
    /// </summary>
    private static async Task OrderCancelled(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<FulfilmentDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var orderId = p.GetProperty("orderId").GetGuid();
        foreach (var a in await db.Allocations.Where(a => a.OrderId == orderId && a.State == AllocationState.Allocated).ToListAsync(ct))
        {
            a.State = AllocationState.RetireRequested;
            a.Intent = "retire";
            a.NextAttemptAt = now;
            // Nobody needs the secrets of a device that will not be sent.
            a.ClaimToken = null;
            a.Credential = null;
        }
    }
}