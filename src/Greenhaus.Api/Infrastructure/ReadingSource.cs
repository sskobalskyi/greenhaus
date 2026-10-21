using Greenhaus.Api.Persistence;

namespace Greenhaus.Api.Infrastructure;

public sealed record ChannelReading(string DeviceId, string ChannelId, double Value, DateTimeOffset At);

/// <summary>Where the latest readings come from. Pulse in production, a fake in tests.</summary>
public interface IReadingSource
{
    Task<IReadOnlyList<ChannelReading>> LatestAsync(IReadOnlyCollection<ChannelEntity> channels, CancellationToken ct);
}

/// <summary>Used until a Pulse client is configured: nothing has been measured.</summary>
public sealed class NoReadings : IReadingSource
{
    public Task<IReadOnlyList<ChannelReading>> LatestAsync(IReadOnlyCollection<ChannelEntity> channels, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChannelReading>>([]);
}