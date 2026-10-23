using Greenhaus.Api.Contracts;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

/// <summary>The state of a pot, per zone and never averaged. Used by the status endpoint and by the assistant's context.</summary>
public static class StatusBuilder
{
    /// <summary>A reading older than this means the controller stopped reporting.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);
    public static async Task<PotStatusDto> BuildAsync(GreenhausDbContext db, IReadingSource readings, TimeProvider clock, Guid potId, CancellationToken ct)
    {
        var zones = await db.Zones.AsNoTracking().Where(z => z.PotId == potId).OrderBy(z => z.Row).ThenBy(z => z.Col).ToListAsync(ct);
        var channels = await db.Channels.AsNoTracking().Where(c => c.PotId == potId && c.Active).ToListAsync(ct);
        var targets = (await db.Requirements.AsNoTracking().Where(r => r.PotId == potId).ToListAsync(ct)).ToDictionary(r => r.Metric, r => r.ToDraft());
        var latest = (await readings.LatestAsync(channels, ct)).ToDictionary(r => (r.DeviceId, r.ChannelId));
        var now = clock.GetUtcNow();

        var zoneStatuses = new List<ZoneStatusDto>();
        var alerts = new List<ActiveAlertDto>();
        foreach (var zone in zones)
        {
            var metrics = new List<MetricReadingDto>();
            foreach (var channel in channels.Where(c => c.ZoneId == zone.Id).OrderBy(c => c.Metric))
            {
                if (!latest.TryGetValue((channel.DeviceId, channel.ChannelId), out var reading))
                {
                    metrics.Add(new MetricReadingDto { Metric = channel.Metric, ChannelId = channel.ChannelId, Unit = channel.Unit, State = ReadingState.Unknown });
                    continue;
                }
                var stale = now - reading.At > Stale;
                var (state, direction) = stale ? (ReadingState.Offline, null) : ReadingEvaluator.Evaluate(reading.Value, targets.GetValueOrDefault(channel.Metric));
                metrics.Add(new MetricReadingDto { Metric = channel.Metric, ChannelId = channel.ChannelId, Value = reading.Value, Unit = channel.Unit, State = state, Direction = direction, UpdatedAt = reading.At });
                if (state == ReadingState.Attention)
                    alerts.Add(new ActiveAlertDto { Id = $"{channel.ChannelId}:{direction}", Metric = channel.Metric, ZoneId = zone.Id, Since = reading.At, Message = $"{MetricNames.Key(channel.Metric)} is {direction} the target" });
            }
            zoneStatuses.Add(new ZoneStatusDto { ZoneId = zone.Id, State = ReadingEvaluator.Worst(metrics.Select(m => m.State)), Metrics = metrics });
        }

        var state2 = ReadingEvaluator.Worst(zoneStatuses.Select(z => z.State));
        var worst = zoneStatuses.FirstOrDefault(z => z.State == state2 && state2 is ReadingState.Attention or ReadingState.Offline);
        return new PotStatusDto { PotId = potId, State = state2, WorstZoneId = worst?.ZoneId, Zones = zoneStatuses, Alerts = alerts };
    }
}