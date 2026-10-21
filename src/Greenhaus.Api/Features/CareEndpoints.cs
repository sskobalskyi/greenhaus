using Greenhaus.Api.Auth;
using Greenhaus.Api.Contracts;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

public static class CareEndpoints
{
    public static void MapCare(this RouteGroupBuilder api)
    {
        api.MapGet("/pots/{potId:guid}/care-tasks", async (Guid potId, string? state, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            if (state is not (null or "open" or "done")) return Problems.Invalid("State must be open or done");
            var query = db.Tasks.AsNoTracking().Where(t => t.PotId == potId);
            if (state is not null) query = query.Where(t => t.State == state);
            return Results.Ok((await query.OrderBy(t => t.DueAt).ToListAsync(ct)).Select(t => t.ToDto()).ToList());
        });

        api.MapPost("/pots/{potId:guid}/care-tasks", async (Guid potId, CareTaskCreate body, HttpContext http, GreenhausDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Length > 80 || body.DueAt is null) return Problems.Invalid("A title (1 to 80 characters) and a due date are required");
            if (body.RepeatEveryDays is < 1) return Problems.Invalid("A repeat interval is at least one day");
            var task = new CareTaskEntity
            {
                Id = Guid.NewGuid(), PotId = potId, Kind = "custom", Title = body.Title.Trim(), DueAt = body.DueAt.Value, State = "open",
                Origin = "user", RepeatEveryDays = body.RepeatEveryDays,
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/greenhaus/v1/pots/{potId}/care-tasks/{task.Id}", task.ToDto());
        });

        api.MapPost("/pots/{potId:guid}/care-tasks/{taskId:guid}/complete", async (Guid potId, Guid taskId, HttpContext http, GreenhausDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.PotId == potId, ct);
            if (task is null) return Problems.NotFound("Unknown task");
            if (task.State == "open")
            {
                var now = clock.GetUtcNow();
                task.State = "done";
                task.CompletedAt = now;
                // A repeating task leaves the next one behind, so a fertilizing schedule keeps going.
                if (task.RepeatEveryDays is { } days)
                {
                    db.Tasks.Add(new CareTaskEntity
                    {
                        Id = Guid.NewGuid(), PotId = potId, Kind = task.Kind, Title = task.Title, DueAt = now.AddDays(days), State = "open",
                        Origin = task.Origin, RepeatEveryDays = days,
                    });
                }
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(task.ToDto());
        });
    }
}