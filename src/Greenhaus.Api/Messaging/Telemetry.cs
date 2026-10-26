using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Greenhaus.Api.Messaging;

/// <summary>The traces and metrics the messaging side emits. Exported when an OpenTelemetry endpoint is configured.</summary>
public static class GreenhausTelemetry
{
    public const string Name = "Greenhaus";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> OutboxPublished = Meter.CreateCounter<long>("greenhaus.outbox.published", description: "Integration events published to the broker");
    public static readonly Counter<long> OutboxFailures = Meter.CreateCounter<long>("greenhaus.outbox.failures", description: "Failed attempts to publish an event");
    public static readonly Counter<long> InboxProcessed = Meter.CreateCounter<long>("greenhaus.inbox.processed", description: "Integration events handled");
    public static readonly Counter<long> InboxDuplicates = Meter.CreateCounter<long>("greenhaus.inbox.duplicates", description: "Redelivered events skipped because they were already handled");
    public static readonly Counter<long> InboxDeadLettered = Meter.CreateCounter<long>("greenhaus.inbox.dead_lettered", description: "Events that could not be handled and went to the dead-letter queue");
    public static readonly Counter<long> LimitsReached = Meter.CreateCounter<long>("greenhaus.limits.reached", description: "Tier limits users ran into");
    public static readonly Counter<long> AssistantQuestions = Meter.CreateCounter<long>("greenhaus.assistant.questions", description: "Questions put to the assistant, by how they ended");
    public static readonly Counter<long> Notifications = Meter.CreateCounter<long>("greenhaus.notifications.created", description: "Notifications created for people");
    public static readonly Counter<long> DevicesClaimed = Meter.CreateCounter<long>("greenhaus.devices.claimed", description: "Controllers claimed");
}