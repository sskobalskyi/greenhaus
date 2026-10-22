namespace Greenhaus.Fulfilment.Contracts;

public class Problem
{
    public string? Type { get; set; }
    public required string Title { get; set; }
    public required int Status { get; set; }
    public string? Detail { get; set; }
    public required string Code { get; set; }
}

/// <summary>One device of an order. The secrets are present only while the device is in the packing room.</summary>
public class AllocationDto
{
    public required string DeviceId { get; set; }
    public Guid OrderId { get; set; }
    public required string Sku { get; set; }
    public required string Model { get; set; }
    public required string State { get; set; }
    public DateTimeOffset AllocatedAt { get; set; }
    public DateTimeOffset ClaimTokenExpiresAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public string? LastError { get; set; }
    public string? ClaimToken { get; set; }
    public string? ClaimUrl { get; set; }
    public string? Credential { get; set; }
}