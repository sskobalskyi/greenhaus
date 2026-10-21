using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Infrastructure;

/// <summary>Keeps Pulse in step with what the pot says. Failures are logged: the pot's own data is the truth and the next change repeats the sync.</summary>
public sealed class DeviceSync(GreenhausDbContext db, IPulseClient pulse, TimeProvider clock, ILogger<DeviceSync> log)
{
    public async Task SyncRulesAsync(Guid potId, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Where(d => d.PotId == potId).ToListAsync(ct);
        if (devices.Count == 0) return;
        var channels = await db.Channels.AsNoTracking().Where(c => c.PotId == potId).ToListAsync(ct);
        var requirements = await db.Requirements.AsNoTracking().Where(r => r.PotId == potId).ToDictionaryAsync(r => r.Metric, ct);

        foreach (var device in devices)
        {
            var rules = RuleBuilder.For(channels.Where(c => c.DeviceId == device.DeviceId), requirements);
            try { await pulse.SetRulesAsync(device.DeviceId, rules, ct); }
            catch (PulseException e) { log.LogWarning("Rules of {Device} were not synced: {Code}", device.DeviceId, e.Code); }
        }
    }

    public async Task SetKillSwitchAsync(Guid potId, bool enabled, CancellationToken ct)
    {
        var actuators = await db.Actuators.AsNoTracking().Where(a => a.PotId == potId).ToListAsync(ct);
        // One command per controller is enough, the switch stops every actuator on it.
        foreach (var actuator in actuators.GroupBy(a => a.DeviceId).Select(g => g.First()))
        {
            try { await pulse.SetKillSwitchAsync(actuator.DeviceId, actuator.Id, enabled, Guid.NewGuid(), clock.GetUtcNow().AddMinutes(10), ct); }
            catch (PulseException e) { log.LogWarning("Kill switch was not sent to {Device}: {Code}", actuator.DeviceId, e.Code); }
        }
    }
}

public static class PulseProblems
{
    /// <summary>What Pulse refused becomes the same kind of problem the contract documents, with Pulse's code kept so clients can switch on it.</summary>
    public static IResult From(PulseException e) => e.Status switch
    {
        400 => Problems.Fail(400, e.Code, e.Message),
        404 => Problems.NotFound("Unknown device"),
        409 => Problems.Fail(409, e.Code, e.Message),
        429 => Problems.Fail(429, "rate_limited", "Too many attempts, try again shortly"),
        _ => Problems.Fail(503, "pulse_unavailable", "The device platform is not available right now"),
    };
}