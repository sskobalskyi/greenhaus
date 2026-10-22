using System.Diagnostics;
using System.Text.Json;
using Greenhaus.Fulfilment.Persistence;

namespace Greenhaus.Fulfilment.Infrastructure;

/// <summary>Records integration events in the outbox. A relay publishes them to the broker, so a lost broker never loses an event.</summary>
public sealed class EventPublisher(FulfilmentDbContext db, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Adds the event to the current unit of work. It is published only if that work is saved.</summary>
    public void Stage(string type, object payload) =>
        db.Outbox.Add(new OutboxMessageEntity
        {
            Id = Guid.NewGuid(), Type = type, Payload = JsonSerializer.Serialize(payload, Json), CreatedAt = clock.GetUtcNow(), Traceparent = Activity.Current?.Id,
        });
}