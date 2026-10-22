using System.Diagnostics;
using System.Text.Json;
using Greenhaus.Billing.Persistence;

namespace Greenhaus.Billing.Infrastructure;

/// <summary>Records integration events in the outbox. A relay publishes them to the broker, so a lost broker never loses an event.</summary>
public sealed class EventPublisher(BillingDbContext db, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Adds the event to the current unit of work. It is published only if that work is saved.</summary>
    public void Stage(string type, object payload) =>
        db.Outbox.Add(new OutboxMessageEntity
        {
            Id = Guid.NewGuid(), Type = type, Payload = JsonSerializer.Serialize(payload, Json), CreatedAt = clock.GetUtcNow(), Traceparent = Activity.Current?.Id,
        });

    public void SubscriptionChanged(string userId, Greenhaus.Domain.Tier tier, string status) =>
        Stage("subscription.changed.v1", new { userId, tier = Name(tier), status, at = clock.GetUtcNow() });

    public void GrantChanged(GrantEntity grant) =>
        Stage("grant.status_changed.v1", new
        {
            grantId = grant.Id, userId = grant.UserId, orderId = grant.OrderId, tier = Name(grant.Tier), status = grant.Status,
            graceEndsAt = grant.GraceEndsAt, at = clock.GetUtcNow(),
        });

    public static string Name(Greenhaus.Domain.Tier tier) => tier.ToString().ToLowerInvariant();
}