using System.Text.Json;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;

namespace Greenhaus.Api.Infrastructure;

/// <summary>Records integration events in the outbox. A relay publishes them to the broker, so a lost broker never loses an event.</summary>
public sealed class EventPublisher(GreenhausDbContext db, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public Task LimitReachedAsync(string userId, string limitType, Tier tier, CancellationToken ct) =>
        EnqueueAsync("limit.reached.v1", new { userId, limitType, tier = tier.ToString().ToLowerInvariant(), occurredAt = clock.GetUtcNow() }, ct);

    public async Task EnqueueAsync(string type, object payload, CancellationToken ct)
    {
        db.Outbox.Add(new OutboxMessageEntity { Id = Guid.NewGuid(), Type = type, Payload = JsonSerializer.Serialize(payload, Json), CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
    }
}