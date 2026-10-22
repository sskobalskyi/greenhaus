namespace Greenhaus.Fulfilment.Persistence;

public class AllocationEntity
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public required string CustomerId { get; set; }
    public required string Sku { get; set; }
    /// <summary>Which of the `quantity` devices of the line this is. With the order and the SKU it makes an allocation unique.</summary>
    public int Index { get; set; }
    public required string DeviceId { get; set; }
    public required string Model { get; set; }
    public required string State { get; set; }
    /// <summary>What Pulse still has to be told: "ship" or "retire". Kept when the sync fails, so a retry knows.</summary>
    public string? Intent { get; set; }

    /// <summary>Plaintext secrets, kept only while the device is in the packing room. Erased once Pulse accepts the shipment or the retirement.</summary>
    public string? ClaimToken { get; set; }
    public string? Credential { get; set; }

    public required string ClaimTokenHash { get; set; }
    public DateTimeOffset ClaimTokenExpiresAt { get; set; }
    public required string CredentialHash { get; set; }
    public required string Capabilities { get; set; }

    public DateTimeOffset AllocatedAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public int PulseAttempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
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
    public string? Traceparent { get; set; }
}

/// <summary>A message already handled by a consumer. The pair is unique, so a redelivery is recognised and skipped.</summary>
public class InboxMessageEntity
{
    public Guid MessageId { get; set; }
    public required string Consumer { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}