using Greenhaus.Domain;

namespace Greenhaus.Billing.Persistence;

/// <summary>The paid subscription of a user. One row per user: subscribing again replaces it.</summary>
public class SubscriptionEntity
{
    public required string UserId { get; set; }
    public Guid Id { get; set; }
    public Tier Tier { get; set; }
    /// <summary>"active", "past_due" or "cancelled".</summary>
    public required string Status { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    /// <summary>The demo provider's token. One ending in "-renewal-fails" makes the next renewal fail.</summary>
    public required string PaymentToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>When the payment first failed, while past due. The dunning period counts from here. Null otherwise.</summary>
    public DateTimeOffset? PastDueSince { get; set; }
    /// <summary>When billing tries the payment method next, while past due.</summary>
    public DateTimeOffset? NextRetryAt { get; set; }
    /// <summary>How many retries failed since the subscription became past due.</summary>
    public int RetryCount { get; set; }
}

public class InvoiceEntity
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    /// <summary>"paid", "failed" or "void".</summary>
    public required string Status { get; set; }
}

/// <summary>A tier earned by an order. One per order id.</summary>
public class GrantEntity
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public Guid OrderId { get; set; }
    public Tier Tier { get; set; }
    /// <summary>"active", "at_risk" or "revoked".</summary>
    public required string Status { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public DateTimeOffset? GraceEndsAt { get; set; }
    /// <summary>
    /// Why the grant is at risk: "returned" (the goods were sent back, which a cancelled return can undo) or "refunded" (the money went
    /// back, which nothing undoes). Null when the grant is not at risk. A grant that was at risk before this column existed has none and
    /// is never restored.
    /// </summary>
    public string? AtRiskReason { get; set; }
}

/// <summary>The entitlement last announced for a user, so a change is announced once and with the right previous tier.</summary>
public class EntitlementStateEntity
{
    public required string UserId { get; set; }
    public Tier Tier { get; set; }
    public DateTimeOffset? Until { get; set; }
    /// <summary>The status of the source the tier comes from: "active", "past_due" or "at_risk". Null on the free tier.</summary>
    public string? Status { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An integration event waiting to be published, written in the same transaction as the change that caused it.</summary>
public class OutboxMessageEntity
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    /// <summary>W3C trace context of the request that caused the event, so a trace continues in the consumers.</summary>
    public string? Traceparent { get; set; }
}

/// <summary>A message already handled by a consumer. The pair is unique, so a redelivery is recognised and skipped.</summary>
public class InboxMessageEntity
{
    public Guid MessageId { get; set; }
    public required string Consumer { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
