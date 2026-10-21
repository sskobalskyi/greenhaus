using System.Diagnostics;
using System.Text.Json;
using Greenhaus.Api.Messaging;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;

namespace Greenhaus.Api.Infrastructure;

/// <summary>Records integration events in the outbox. A relay publishes them to the broker, so a lost broker never loses an event.</summary>
public sealed class EventPublisher(GreenhausDbContext db, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async Task LimitReachedAsync(string userId, string limitType, Tier tier, CancellationToken ct)
    {
        GreenhausTelemetry.LimitsReached.Add(1, new KeyValuePair<string, object?>("limit", limitType));
        await EnqueueAsync("limit.reached.v1", new { userId, limitType, tier = tier.ToString().ToLowerInvariant(), at = clock.GetUtcNow() }, ct);
    }

    /// <summary>Adds the event to the current unit of work. It is published only if that work is saved.</summary>
    public void Stage(string type, object payload) =>
        db.Outbox.Add(new OutboxMessageEntity
        {
            Id = Guid.NewGuid(), Type = type, Payload = JsonSerializer.Serialize(payload, Json), CreatedAt = clock.GetUtcNow(), Traceparent = Activity.Current?.Id,
        });

    public async Task EnqueueAsync(string type, object payload, CancellationToken ct)
    {
        Stage(type, payload);
        await db.SaveChangesAsync(ct);
    }
}