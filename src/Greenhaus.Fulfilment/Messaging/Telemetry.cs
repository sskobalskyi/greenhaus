using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Greenhaus.Fulfilment.Messaging;

/// <summary>The traces and metrics of fulfilment. Exported when an OpenTelemetry endpoint is configured.</summary>
public static class FulfilmentTelemetry
{
    public const string Name = "Greenhaus.Fulfilment";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Allocated = Meter.CreateCounter<long>("greenhaus.fulfilment.allocated", description: "Devices allocated to orders");
    public static readonly Counter<long> Shipped = Meter.CreateCounter<long>("greenhaus.fulfilment.shipped", description: "Devices Pulse accepted as shipped");
    public static readonly Counter<long> Retired = Meter.CreateCounter<long>("greenhaus.fulfilment.retired", description: "Devices retired before shipping");
    public static readonly Counter<long> PulseRetries = Meter.CreateCounter<long>("greenhaus.fulfilment.pulse_retries", description: "Calls to Pulse that will be tried again");
    public static readonly Counter<long> PulseFailed = Meter.CreateCounter<long>("greenhaus.fulfilment.pulse_failed", description: "Devices Pulse never accepted");
    public static readonly Counter<long> OutboxPublished = Meter.CreateCounter<long>("greenhaus.outbox.published", description: "Integration events published to the broker");
    public static readonly Counter<long> OutboxFailures = Meter.CreateCounter<long>("greenhaus.outbox.failures", description: "Failed attempts to publish an event");
    public static readonly Counter<long> InboxProcessed = Meter.CreateCounter<long>("greenhaus.inbox.processed", description: "Integration events handled");
    public static readonly Counter<long> InboxDuplicates = Meter.CreateCounter<long>("greenhaus.inbox.duplicates", description: "Redelivered events skipped because they were already handled");
    public static readonly Counter<long> InboxDeadLettered = Meter.CreateCounter<long>("greenhaus.inbox.dead_lettered", description: "Events that could not be handled and went to the dead-letter queue");
}