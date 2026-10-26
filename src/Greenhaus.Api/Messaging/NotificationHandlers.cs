using System.Globalization;
using System.Text.Json;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Messaging;

/// <summary>Turns events from Pulse and billing into notifications for the owner of the pot or the account they are about.</summary>
public static class NotificationHandlers
{
    /// <summary>A controller that keeps dropping and reconnecting is told about once in this window, not every time.</summary>
    public static readonly TimeSpan StatusWindow = TimeSpan.FromMinutes(15);

    public static async Task AlertAsync(JsonElement p, bool raised, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<GreenhausDbContext>();
        var deviceId = p.GetProperty("deviceId").GetString()!;
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null) return; // a device nobody claimed, or one that was retired

        var channelId = p.GetProperty("channelId").GetString()!;
        var parameters = new Dictionary<string, string> { ["metric"] = p.GetProperty("metric").GetString()!, ["deviceId"] = deviceId };
        if (p.TryGetProperty("rule", out var rule) && rule.ValueKind == JsonValueKind.String) parameters["rule"] = rule.GetString()!;
        if (p.TryGetProperty("direction", out var direction) && direction.ValueKind == JsonValueKind.String) parameters["direction"] = direction.GetString()!;
        if (p.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number) parameters["value"] = value.GetDouble().ToString("0.##", CultureInfo.InvariantCulture);

        // In the zone view the alert says which zone, by position (a zone's label is typed text, so it stays out).
        var pot = await db.Pots.AsNoTracking().FirstAsync(x => x.Id == device.PotId, ct);
        if (pot.ViewMode == ViewMode.Zones)
        {
            var zone = await db.Channels.AsNoTracking().Where(c => c.DeviceId == deviceId && c.ChannelId == channelId)
                .Join(db.Zones, c => c.ZoneId, z => z.Id, (_, z) => z).FirstOrDefaultAsync(ct);
            if (zone is not null) { parameters["zoneRow"] = (zone.Row + 1).ToString(CultureInfo.InvariantCulture); parameters["zoneCol"] = (zone.Col + 1).ToString(CultureInfo.InvariantCulture); }
        }

        // One notification for the start and one for the end of an alert, however often the message is delivered.
        var alertId = p.GetProperty("alertId").GetString()!;
        await services.GetRequiredService<UserNotifier>().NotifyOnceAsync(device.OwnerId, device.PotId, raised ? "alert_raised" : "alert_resolved",
            parameters, $"alert:{alertId}:{(raised ? "raised" : "resolved")}", null, ct);
    }

    public static async Task DeviceStatusAsync(JsonElement p, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<GreenhausDbContext>();
        var deviceId = p.GetProperty("deviceId").GetString()!;
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null) return;

        var offline = p.GetProperty("status").GetString() == "offline";
        var parameters = new Dictionary<string, string> { ["deviceId"] = deviceId };
        if (p.TryGetProperty("cause", out var cause) && cause.ValueKind == JsonValueKind.String) parameters["cause"] = cause.GetString()!;
        await services.GetRequiredService<UserNotifier>().NotifyOnceAsync(device.OwnerId, device.PotId, offline ? "device_offline" : "device_online",
            parameters, $"device:{deviceId}:{(offline ? "offline" : "online")}", StatusWindow, ct);
    }

    /// <summary>A lower tier is told as a plan change, a grant that is at risk as a deadline.</summary>
    public static async Task PlanAsync(JsonElement p, bool applied, IServiceProvider services, CancellationToken ct)
    {
        if (!applied) return; // an old event that arrived late says nothing new
        var userId = p.GetProperty("userId").GetString()!;
        var to = Enum.Parse<Tier>(p.GetProperty("tier").GetString()!, ignoreCase: true);
        var from = Enum.Parse<Tier>(p.GetProperty("previousTier").GetString()!, ignoreCase: true);
        var reason = p.GetProperty("reason").GetString();
        var notifier = services.GetRequiredService<UserNotifier>();
        var at = p.GetProperty("at").GetDateTimeOffset().UtcTicks.ToString(CultureInfo.InvariantCulture);

        if (reason == "grant_at_risk")
        {
            var until = p.TryGetProperty("validUntil", out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset().ToString("O", CultureInfo.InvariantCulture) : null;
            var parameters = new Dictionary<string, string> { ["tier"] = to.ToString().ToLowerInvariant() };
            if (until is not null) parameters["until"] = until;
            await notifier.NotifyOnceAsync(userId, null, "plan_at_risk", parameters, $"plan:{at}:at_risk", null, ct);
        }
        else if (to < from)
        {
            await notifier.NotifyOnceAsync(userId, null, "plan_changed",
                new Dictionary<string, string> { ["from"] = from.ToString().ToLowerInvariant(), ["to"] = to.ToString().ToLowerInvariant() },
                $"plan:{at}:changed", null, ct);
        }
    }
}