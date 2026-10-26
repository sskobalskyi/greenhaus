using Greenhaus.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Infrastructure;

/// <summary>A place in the monthly quota that a running question holds until it is answered or given up.</summary>
public sealed record QuotaReservation(string UserId, DateOnly Month);

/// <summary>
/// Questions the assistant answered for a user this month (the calendar month in UTC), and the places held by questions that are still
/// running. A place is taken with one statement before a question is sent and given back, or turned into a counted answer, when it ends,
/// so questions that start together cannot all pass a check that none of them has counted in yet. The number shown to the person is the
/// count of answered questions only.
/// </summary>
public static class AssistantUsage
{
    /// <summary>
    /// How long a held place is trusted. A process that died in the middle of a stream never gives its place back, so a place older than
    /// this is dropped the next time the person asks. A real answer takes seconds, so this is far longer than any stream.
    /// </summary>
    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(10);

    public static DateOnly MonthOf(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month, 1);

    /// <summary>The answered questions this month. Held places are not part of it.</summary>
    public static async Task<int> CountAsync(GreenhausDbContext db, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var month = MonthOf(now);
        return await db.AssistantUsage.AsNoTracking().Where(u => u.UserId == userId && u.Month == month).Select(u => u.Count).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Takes a place if answered questions plus the places held by running ones are still under the limit, in one statement that the
    /// database serialises per user and month. Null means the quota is used up.
    /// </summary>
    public static async Task<QuotaReservation?> TryReserveAsync(GreenhausDbContext db, string userId, DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (limit <= 0) return null;
        var month = MonthOf(now);
        var stale = now - ReservationLifetime;
        var taken = await db.Database.SqlQuery<int>(
            $"""
            INSERT INTO greenhaus.assistant_usage AS a ("UserId", "Month", "Count", "Reserved", "ReservedAt") VALUES ({userId}, {month}, 0, 1, {now})
            ON CONFLICT ("UserId", "Month") DO UPDATE SET
                "Reserved" = CASE WHEN a."ReservedAt" IS NULL OR a."ReservedAt" < {stale} THEN 1 ELSE a."Reserved" + 1 END,
                "ReservedAt" = {now}
            WHERE a."Count" + CASE WHEN a."ReservedAt" IS NULL OR a."ReservedAt" < {stale} THEN 0 ELSE a."Reserved" END < {limit}
            RETURNING 1 AS "Value"
            """).ToListAsync(ct);
        return taken.Count == 1 ? new QuotaReservation(userId, month) : null;
    }

    /// <summary>The question was answered: its place becomes a counted question. One statement, so answers finishing together are all counted.</summary>
    public static Task ConfirmAsync(GreenhausDbContext db, QuotaReservation r, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE greenhaus.assistant_usage SET "Count" = "Count" + 1, "Reserved" = GREATEST("Reserved" - 1, 0) WHERE "UserId" = {r.UserId} AND "Month" = {r.Month}""", ct);

    /// <summary>The question ended without an answer (a fallback, a failure, the person went away): its place is free again.</summary>
    public static Task ReleaseAsync(GreenhausDbContext db, QuotaReservation r, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE greenhaus.assistant_usage SET "Reserved" = GREATEST("Reserved" - 1, 0) WHERE "UserId" = {r.UserId} AND "Month" = {r.Month}""", ct);
}