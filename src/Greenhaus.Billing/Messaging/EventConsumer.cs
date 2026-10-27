using System.Diagnostics;
using System.Text.Json;
using Greenhaus.Billing.Infrastructure;
using Greenhaus.Billing.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Greenhaus.Billing.Messaging;

/// <summary>
/// Consumes the events Greenhaus acts on. Each message is handled once: the handler's changes and the inbox row that records
/// the message id are saved in one transaction, so a redelivery finds the row and is skipped. A message that cannot be
/// handled is retried once and then goes to the dead-letter queue instead of blocking the others.
/// </summary>
public sealed class EventConsumer(IServiceScopeFactory scopes, IOptions<RabbitOptions> options, ILogger<EventConsumer> log) : BackgroundService
{
    public const string ConsumerName = "greenhaus-billing";

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
        using var activity = BillingTelemetry.Source.StartActivity($"{delivery.RoutingKey} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", delivery.RoutingKey);

        try
        {
            var id = Guid.TryParse(Rabbit.Text(delivery.BasicProperties.Headers, "messageId") ?? delivery.BasicProperties.MessageId, out var parsed) ? parsed : (Guid?)null;
            if (id is null) throw new InvalidDataException("The message has no message id");
            activity?.SetTag("messaging.message.id", id.ToString());

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            if (await db.Inbox.AnyAsync(m => m.MessageId == id && m.Consumer == ConsumerName, ct))
            {
                BillingTelemetry.InboxDuplicates.Add(1);
            }
            else
            {
                using var payload = JsonDocument.Parse(delivery.Body);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await EventHandlers.HandleAsync(delivery.RoutingKey, payload.RootElement, scope.ServiceProvider, ct);
                db.Inbox.Add(new InboxMessageEntity { MessageId = id.Value, Consumer = ConsumerName, ProcessedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                BillingTelemetry.InboxProcessed.Add(1, new KeyValuePair<string, object?>("type", delivery.RoutingKey));
            }
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A duplicate that raced past the check collides on the inbox key: it was handled, so acknowledge it.
            if (e is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } } && e.ToString().Contains("inbox"))
            {
                BillingTelemetry.InboxDuplicates.Add(1);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                return;
            }
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            var firstFailure = !delivery.Redelivered && e is not (InvalidDataException or JsonException or KeyNotFoundException);
            if (!firstFailure) BillingTelemetry.InboxDeadLettered.Add(1, new KeyValuePair<string, object?>("type", delivery.RoutingKey));
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
        "order.returned.v1" or "order.refunded.v1" => OrderReversed(routingKey, payload, services, ct),
        "order.return_cancelled.v1" => ReturnCancelled(payload, services, ct),
        _ => throw new InvalidDataException($"No handler for {routingKey}"),
    };

    /// <summary>
    /// A confirmed order of enough value earns a grant. One per order, so a redelivered or repeated event changes nothing. A grant of
    /// a tier the user already holds starts when the running one ends, so grants extend each other instead of stacking.
    /// </summary>
    private static async Task OrderConfirmed(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<BillingDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();
        var events = services.GetRequiredService<EventPublisher>();
        var user = p.GetProperty("customerId").GetString()!;
        var orderId = p.GetProperty("orderId").GetGuid();
        var total = p.GetProperty("total").GetDecimal();

        // The thresholds are in dollars. An order in another currency earns nothing until there is a rate to convert it with.
        if (p.GetProperty("currency").GetString() != "USD") return;
        if (GrantPolicy.For(total, services.GetRequiredService<IOptions<BillingOptions>>().Value.GrantRules) is not { } rule) return;
        if (await db.Grants.AnyAsync(g => g.OrderId == orderId, ct)) return;

        var now = clock.GetUtcNow();
        var running = await db.Grants.Where(g => g.UserId == user && g.Tier == rule.Tier && g.Status != "revoked" && g.ValidUntil > now)
            .Select(g => (DateTimeOffset?)g.ValidUntil).MaxAsync(ct);
        var from = running ?? now;
        var grant = new GrantEntity
        {
            Id = Guid.NewGuid(), UserId = user, OrderId = orderId, Tier = rule.Tier, Status = "active",
            ValidFrom = from, ValidUntil = from.AddDays(30 * rule.Months),
        };
        db.Grants.Add(grant);
        events.GrantChanged(grant);
        BillingTelemetry.GrantsCreated.Add(1, new KeyValuePair<string, object?>("tier", EventPublisher.Name(rule.Tier)));
        await db.SaveChangesAsync(ct);
        await services.GetRequiredService<EntitlementService>().RecomputeAsync(user, "grant_started", ct);
    }

    /// <summary>
    /// A return or a refund puts the grant of that order at risk for the grace period. A refund can also cancel an order that was
    /// never shipped, which has the same effect. A grant that is already at risk keeps its grace period, but a refund arriving after a
    /// return takes away the chance to undo it. Anything else (no grant, revoked) changes nothing.
    /// </summary>
    private static async Task OrderReversed(string routingKey, JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<BillingDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();
        var orderId = p.GetProperty("orderId").GetGuid();
        var reason = routingKey == "order.refunded.v1" ? "refunded" : "returned";

        var grant = await db.Grants.FirstOrDefaultAsync(g => g.OrderId == orderId, ct);
        if (grant is { Status: "at_risk" } && reason == "refunded" && grant.AtRiskReason != "refunded")
        {
            grant.AtRiskReason = "refunded";
            await db.SaveChangesAsync(ct);
            return;
        }
        if (grant is not { Status: "active" }) return;

        grant.AtRiskReason = reason;
        grant.Status = "at_risk";
        grant.GraceEndsAt = clock.GetUtcNow() + services.GetRequiredService<IOptions<BillingOptions>>().Value.Grace;
        services.GetRequiredService<EventPublisher>().GrantChanged(grant);
        BillingTelemetry.GrantsChanged.Add(1, new KeyValuePair<string, object?>("status", "at_risk"));
        await db.SaveChangesAsync(ct);
        await services.GetRequiredService<EntitlementService>().RecomputeAsync(grant.UserId, "grant_at_risk", ct);
    }

    /// <summary>
    /// A return that was registered but not completed was cancelled: the goods stay with the customer. The grant that the return put at
    /// risk goes back to active with the end date it had, and the deadline is cleared. It is not restored when the grant is at risk
    /// because of a refund, when its grace period is over (the job revokes it on its next run and a revoked grant stays revoked), or
    /// when it is not at risk at all. A repeated event finds an active grant and changes nothing.
    /// </summary>
    private static async Task ReturnCancelled(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<BillingDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();
        var orderId = p.GetProperty("orderId").GetGuid();

        var grant = await db.Grants.FirstOrDefaultAsync(g => g.OrderId == orderId, ct);
        if (grant is not { Status: "at_risk", AtRiskReason: "returned" } || grant.GraceEndsAt is not { } grace || grace <= clock.GetUtcNow()) return;

        grant.Status = "active";
        grant.GraceEndsAt = null;
        grant.AtRiskReason = null;
        services.GetRequiredService<EventPublisher>().GrantChanged(grant);
        BillingTelemetry.GrantsChanged.Add(1, new KeyValuePair<string, object?>("status", "active"));
        await db.SaveChangesAsync(ct);
        await services.GetRequiredService<EntitlementService>().RecomputeAsync(grant.UserId, "grant_started", ct);
    }
}