using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Greenhaus.Billing.Messaging;

/// <summary>The traces and metrics billing emits. Exported when an OpenTelemetry endpoint is configured.</summary>
public static class BillingTelemetry
{
    public const string Name = "Greenhaus.Billing";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> OutboxPublished = Meter.CreateCounter<long>("billing.outbox.published", description: "Integration events published to the broker");
    public static readonly Counter<long> OutboxFailures = Meter.CreateCounter<long>("billing.outbox.failures", description: "Failed attempts to publish an event");
    public static readonly Counter<long> InboxProcessed = Meter.CreateCounter<long>("billing.inbox.processed", description: "Integration events handled");
    public static readonly Counter<long> InboxDuplicates = Meter.CreateCounter<long>("billing.inbox.duplicates", description: "Redelivered events skipped because they were already handled");
    public static readonly Counter<long> InboxDeadLettered = Meter.CreateCounter<long>("billing.inbox.dead_lettered", description: "Events that could not be handled and went to the dead-letter queue");
    public static readonly Counter<long> GrantsCreated = Meter.CreateCounter<long>("billing.grants.created", description: "Purchase grants created from confirmed orders");
    public static readonly Counter<long> GrantsChanged = Meter.CreateCounter<long>("billing.grants.changed", description: "Purchase grants that moved to at risk or revoked");
    public static readonly Counter<long> EntitlementChanges = Meter.CreateCounter<long>("billing.entitlement.changes", description: "Effective entitlements announced");
}