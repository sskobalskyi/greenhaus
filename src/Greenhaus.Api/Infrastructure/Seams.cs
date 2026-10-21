using Greenhaus.Api.Persistence;

namespace Greenhaus.Api.Infrastructure;

public sealed record ChannelReading(string DeviceId, string ChannelId, double Value, DateTimeOffset At);

/// <summary>Where the latest readings come from. Pulse in production, a fake in tests.</summary>
public interface IReadingSource
{
    Task<IReadOnlyList<ChannelReading>> LatestAsync(IReadOnlyCollection<ChannelEntity> channels, CancellationToken ct);
}

/// <summary>Sends a run command to a controller. Pulse in production.</summary>
public interface ICommandSender
{
    Task<Guid> RunAsync(string deviceId, string actuatorId, int seconds, CancellationToken ct);
}