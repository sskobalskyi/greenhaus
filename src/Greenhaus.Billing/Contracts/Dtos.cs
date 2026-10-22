using Greenhaus.Domain;

namespace Greenhaus.Billing.Contracts;

// Wire types of billing-api.yaml. Enums travel as snake_case strings, absent values are omitted.

public class Problem
{
    public required string Title { get; set; }
    public required int Status { get; set; }
    public string? Detail { get; set; }
    public required string Code { get; set; }
}

public class MoneyDto
{
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
}

public class PlanDto
{
    public Tier Tier { get; set; }
    public required MoneyDto PricePerMonth { get; set; }
}

public class SubscribeRequest
{
    public Tier? Tier { get; set; }
    public string? PaymentMethodToken { get; set; }
}

public class SubscriptionDto
{
    public Guid Id { get; set; }
    public Tier Tier { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
}

public class GrantDto
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Tier Tier { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    // Required by the contract, null while there is no grace period running.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public DateTimeOffset? GraceEndsAt { get; set; }
}

public class InvoiceDto
{
    public Guid Id { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public required MoneyDto Amount { get; set; }
    public required string Status { get; set; }
}