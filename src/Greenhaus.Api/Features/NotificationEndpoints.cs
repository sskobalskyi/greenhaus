using System.Text.Json;
using Greenhaus.Api.Auth;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

public sealed class NotificationDto
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public Guid? PotId { get; set; }
    public required Dictionary<string, string> Params { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public static class NotificationEndpoints
{
    public static void MapNotifications(this RouteGroupBuilder api)
    {
        api.MapGet("/notifications", async (bool? unread, int? limit, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (limit is < 1 or > 100) return Problems.Invalid("limit must be 1 to 100");
            var user = http.User.UserId();
            var query = db.Notifications.AsNoTracking().Where(n => n.UserId == user);
            if (unread == true) query = query.Where(n => n.ReadAt == null);
            var rows = await query.OrderByDescending(n => n.CreatedAt).Take(limit ?? 30).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto).ToList());
        });

        api.MapPost("/notifications/{notificationId:guid}/read", async (Guid notificationId, HttpContext http, GreenhausDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            // Someone else's notification does not exist for the caller.
            var user = http.User.UserId();
            var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == user, ct);
            if (notification is null) return Problems.NotFound("Unknown notification");
            if (notification.ReadAt is null)
            {
                notification.ReadAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(ToDto(notification));
        });

        api.MapPost("/notifications/read-all", async (HttpContext http, GreenhausDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            var now = clock.GetUtcNow();
            await db.Notifications.Where(n => n.UserId == user && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, (DateTimeOffset?)now), ct);
            return Results.NoContent();
        });
    }

    private static NotificationDto ToDto(NotificationEntity n) => new()
    {
        Id = n.Id, Kind = n.Kind, PotId = n.PotId, CreatedAt = n.CreatedAt, ReadAt = n.ReadAt,
        Params = JsonSerializer.Deserialize<Dictionary<string, string>>(n.ParamsJson) ?? [],
    };
}