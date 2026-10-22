using Greenhaus.Fulfilment.Domain;
using Greenhaus.Fulfilment.Messaging;
using Greenhaus.Fulfilment.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Greenhaus.Fulfilment.Infrastructure;

/// <summary>
/// Tells Pulse what happened to devices after it announced them: shipped (claimable now) or retired. It runs apart from the message
/// consumer, so a slow or missing Pulse never holds up the queue. Pulse registers a device when it consumes device.allocated.v1,
/// which can be after the order shipped, so a 404 is retried like an outage. Each failure waits longer, up to a limit. After
/// <see cref="FulfilmentOptions.MaxAttempts"/> the device is shown as pulse_failed and waits for someone to ask for a retry.
/// </summary>
public sealed class PulseSyncWorker(IServiceScopeFactory scopes, IOptions<FulfilmentOptions> options, ILogger<PulseSyncWorker> log) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int handled = 0;
            try { handled = await SyncDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning(e, "The Pulse sync pass failed, trying again shortly"); }
            if (handled == 0) await Task.Delay(options.Value.SyncInterval, stoppingToken);
        }
    }

    /// <summary>One pass over the devices whose time has come. Public so tests can run it without waiting for the timer.</summary>
    public async Task<int> SyncDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FulfilmentDbContext>();
        var pulse = scope.ServiceProvider.GetRequiredService<IPulseDevices>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var o = options.Value;
        var now = clock.GetUtcNow();

        var due = await db.Allocations
            .Where(a => (a.State == AllocationState.ShipRequested || a.State == AllocationState.RetireRequested) && (a.NextAttemptAt == null || a.NextAttemptAt <= now))
            .OrderBy(a => a.NextAttemptAt).Take(BatchSize).ToListAsync(ct);

        foreach (var allocation in due)
        {
            try
            {
                if (allocation.State == AllocationState.ShipRequested)
                {
                    await pulse.ShipAsync(allocation.DeviceId, ct);
                    allocation.State = AllocationState.Shipped;
                    allocation.ShippedAt = clock.GetUtcNow();
                    FulfilmentTelemetry.Shipped.Add(1);
                }
                else
                {
                    await pulse.RetireAsync(allocation.DeviceId, ct);
                    allocation.State = AllocationState.Retired;
                    FulfilmentTelemetry.Retired.Add(1);
                }
                // The secrets were shown at packing. Once Pulse has the device in its final state nobody needs them again.
                allocation.ClaimToken = null;
                allocation.Credential = null;
                allocation.Intent = null;
                allocation.NextAttemptAt = null;
                allocation.LastError = null;
            }
            catch (PulseException e)
            {
                allocation.PulseAttempts++;
                allocation.LastError = $"{e.Code} ({e.Status})";
                if (!e.Retriable || allocation.PulseAttempts >= o.MaxAttempts)
                {
                    allocation.State = AllocationState.PulseFailed;
                    allocation.NextAttemptAt = null;
                    FulfilmentTelemetry.PulseFailed.Add(1);
                    log.LogWarning("Pulse did not accept {Intent} of {Device}: {Error}", allocation.Intent, allocation.DeviceId, allocation.LastError);
                }
                else
                {
                    var wait = TimeSpan.FromTicks(Math.Min(o.MaxBackoff.Ticks, o.BaseBackoff.Ticks * (1L << Math.Min(allocation.PulseAttempts - 1, 20))));
                    allocation.NextAttemptAt = clock.GetUtcNow() + wait;
                    FulfilmentTelemetry.PulseRetries.Add(1);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}