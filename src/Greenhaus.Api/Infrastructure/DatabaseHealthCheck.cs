using Greenhaus.Api.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Greenhaus.Api.Infrastructure;

public sealed class DatabaseHealthCheck(GreenhausDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("The database is unreachable");
}