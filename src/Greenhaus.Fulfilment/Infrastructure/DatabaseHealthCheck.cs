using Greenhaus.Fulfilment.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Greenhaus.Fulfilment.Infrastructure;

public sealed class DatabaseHealthCheck(FulfilmentDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("The database is unreachable");
}