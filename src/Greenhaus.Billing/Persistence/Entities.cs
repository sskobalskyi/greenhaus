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
}