namespace Greenhaus.MockServer.Billing;

public class Money
{
    public double Amount { get; set; }
    public required string Currency { get; set; }
}

public class Plan
{
    public Tier Tier { get; set; }
    public required Money PricePerMonth { get; set; }
}

public class SubscribeRequest
{
    public Tier? Tier { get; set; }
    public string? PaymentMethodToken { get; set; }
}

public class Subscription
{
    public Guid Id { get; set; }
    public Tier Tier { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
}

public class Grant
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Tier Tier { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public DateTimeOffset? GraceEndsAt { get; set; }
}

public class Invoice
{
    public Guid Id { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public required Money Amount { get; set; }
    public required string Status { get; set; }
}

/// <summary>Mock-only: creates a purchase grant, as an order event would.</summary>
public class GrantRequest
{
    public Guid? OrderId { get; set; }
    public Tier? Tier { get; set; }
    public int? Months { get; set; }
}

/// <summary>Mock-only: moves the mock clock forward.</summary>
public class AdvanceRequest
{
    public double? Days { get; set; }
}
