using Greenhaus.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Infrastructure;

/// <summary>Questions the assistant answered for a user this month. The month is the calendar month in UTC.</summary>
public static class AssistantUsage
{
    public static DateOnly MonthOf(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month, 1);

    public static async Task<int> CountAsync(GreenhausDbContext db, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var month = MonthOf(now);
        return await db.AssistantUsage.AsNoTracking().Where(u => u.UserId == userId && u.Month == month).Select(u => u.Count).FirstOrDefaultAsync(ct);
    }

    /// <summary>Counts one more answered question. One statement, so two answers finishing together are both counted.</summary>
    public static Task AddAsync(GreenhausDbContext db, string userId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO greenhaus.assistant_usage ("UserId", "Month", "Count") VALUES ({userId}, {MonthOf(now)}, 1) ON CONFLICT ("UserId", "Month") DO UPDATE SET "Count" = greenhaus.assistant_usage."Count" + 1""", ct);
}