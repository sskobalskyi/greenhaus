using System.Text.Json;
using Greenhaus.Api.Messaging;
using Greenhaus.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Infrastructure;

/// <summary>Tells a person that something happened. The text is the client's job, so only a kind and its facts travel.</summary>
public interface IUserNotifier
{
    Task NotifyAsync(string userId, Guid? potId, string kind, IReadOnlyDictionary<string, string> parameters, CancellationToken ct);
}

public sealed class UserNotifier(GreenhausDbContext db, TimeProvider clock) : IUserNotifier
{
    public Task NotifyAsync(string userId, Guid? potId, string kind, IReadOnlyDictionary<string, string> parameters, CancellationToken ct) =>
        AddAsync(userId, potId, kind, parameters, null, null, ct);

    /// <summary>
    /// Tells it once: when a notification with the same key already exists, nothing is added. With a window, an earlier one older
    /// than the window does not count, so a thing that keeps happening is told again after a while but never in a burst.
    /// Returns whether a notification was added.
    /// </summary>
    public Task<bool> NotifyOnceAsync(string userId, Guid? potId, string kind, IReadOnlyDictionary<string, string> parameters, string dedupeKey, TimeSpan? window, CancellationToken ct) =>
        AddAsync(userId, potId, kind, parameters, dedupeKey, window, ct);

    private async Task<bool> AddAsync(string userId, Guid? potId, string kind, IReadOnlyDictionary<string, string> parameters, string? key, TimeSpan? window, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (key is not null)
        {
            var existing = await db.Notifications.Where(n => n.UserId == userId && n.DedupeKey == key).Select(n => (DateTimeOffset?)n.CreatedAt).FirstOrDefaultAsync(ct);
            if (existing is { } at)
            {
                if (window is null || now - at < window) return false;
                // The earlier one is old enough to be told again: it gives its key up.
                await db.Notifications.Where(n => n.UserId == userId && n.DedupeKey == key).ExecuteUpdateAsync(s => s.SetProperty(n => n.DedupeKey, (string?)null), ct);
            }
        }

        db.Notifications.Add(new NotificationEntity
        {
            Id = Guid.NewGuid(), UserId = userId, Kind = kind, PotId = potId, CreatedAt = now, DedupeKey = key,
            ParamsJson = JsonSerializer.Serialize(parameters),
        });
        await db.SaveChangesAsync(ct);
        GreenhausTelemetry.Notifications.Add(1, new KeyValuePair<string, object?>("kind", kind));
        return true;
    }
}