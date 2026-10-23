using System.Globalization;
using Greenhaus.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Greenhaus.Api.Infrastructure;

public sealed class NotificationOptions
{
    /// <summary>How often care tasks are checked for being due.</summary>
    public TimeSpan CareInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Tells the owner once about every open care task whose time has come.</summary>
public sealed class CareReminders(GreenhausDbContext db, UserNotifier notifier, TimeProvider clock)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.Tasks.AsNoTracking().Where(t => t.State == "open" && t.DueAt <= now)
            .Join(db.Pots, t => t.PotId, p => p.Id, (t, p) => new { Task = t, p.OwnerId }).Take(500).ToListAsync(ct);

        var created = 0;
        foreach (var item in due)
        {
            var parameters = new Dictionary<string, string> { ["title"] = item.Task.Title, ["kind"] = item.Task.Kind, ["dueAt"] = item.Task.DueAt.ToString("O", CultureInfo.InvariantCulture) };
            if (await notifier.NotifyOnceAsync(item.OwnerId, item.Task.PotId, "care_task_due", parameters, $"care:{item.Task.Id}", null, ct)) created++;
        }
        return created;
    }
}

public sealed class CareReminderJob(IServiceScopeFactory scopes, IOptions<NotificationOptions> options, ILogger<CareReminderJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CareReminders>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning(e, "The care reminder job failed, trying again later"); }
            await Task.Delay(options.Value.CareInterval, stoppingToken);
        }
    }
}