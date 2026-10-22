using Greenhaus.Billing.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Greenhaus.Billing.Infrastructure;

public sealed class DatabaseHealthCheck(BillingDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("The database is unreachable");
}