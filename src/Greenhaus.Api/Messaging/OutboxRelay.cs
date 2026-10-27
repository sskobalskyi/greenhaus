using System.Diagnostics;
using System.Text;
using Greenhaus.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Greenhaus.Api.Messaging;

/// <summary>
/// Publishes the outbox to the broker. Rows are claimed with FOR UPDATE SKIP LOCKED, so several instances never publish
/// the same row twice at once, and a row is marked published only after the broker has confirmed it. A crash between the
/// two means the event is published again, which consumers tolerate because they deduplicate on the message id.
/// </summary>
public sealed class OutboxRelay(IServiceScopeFactory scopes, IOptions<RabbitOptions> options, ILogger<OutboxRelay> log, TimeProvider clock) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await Rabbit.ConnectAsync(o, stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                await Rabbit.DeclareExchangeAsync(channel, o, stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var published = await PublishBatchAsync(channel, o, stoppingToken);
                    if (published == 0) await Task.Delay(Idle, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception e)
            {
                GreenhausTelemetry.OutboxFailures.Add(1);
                log.LogWarning(e, "The outbox relay lost the broker, retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(IChannel channel, RabbitOptions o, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GreenhausDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.Outbox
            .FromSqlRaw($"""SELECT * FROM {GreenhausDbContext.Schema}.outbox WHERE "PublishedAt" IS NULL ORDER BY "CreatedAt" LIMIT {BatchSize} FOR UPDATE SKIP LOCKED""")
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            ActivityContext.TryParse(message.Traceparent, null, out var parent);
            using var activity = GreenhausTelemetry.Source.StartActivity($"{message.Type} publish", ActivityKind.Producer, parent);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination.name", o.Exchange);
            activity?.SetTag("messaging.rabbitmq.destination.routing_key", message.Type);
            activity?.SetTag("messaging.message.id", message.Id.ToString());

            var headers = new Dictionary<string, object?>
            {
                ["messageId"] = message.Id.ToString(),
                ["occurredAt"] = message.CreatedAt.ToString("O"),
                ["schemaVersion"] = "1",
            };
            // An event with no trace context has no traceparent header. A null header would be a header that is there and says nothing.
            if ((activity?.Id ?? message.Traceparent) is { } traceparent) headers["traceparent"] = traceparent;
            var properties = new BasicProperties
            {
                MessageId = message.Id.ToString(), Type = message.Type, ContentType = "application/json", Persistent = true, Headers = headers,
            };
            try
            {
                await channel.BasicPublishAsync(o.Exchange, message.Type, mandatory: false, properties, Encoding.UTF8.GetBytes(message.Payload), ct);
            }
            catch
            {
                message.Attempts++;
                await db.SaveChangesAsync(CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);
                throw;
            }
            message.PublishedAt = clock.GetUtcNow();
            GreenhausTelemetry.OutboxPublished.Add(1, new KeyValuePair<string, object?>("type", message.Type));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count;
    }
}