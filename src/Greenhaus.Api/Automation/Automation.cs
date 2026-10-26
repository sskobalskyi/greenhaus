using Greenhaus.Api.Features;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Greenhaus.Api.Automation;

public sealed class AutomationOptions
{
    /// <summary>Off in tests that drive the cycle by hand, and wherever the API should not control anything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the loop looks at every pot with an actuator that is not off.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>What the control loop remembers on an actuator, and how a person's own actions reset it.</summary>
public static class ActuatorBook
{
    /// <summary>The run time counted against the daily cap restarts every UTC day.</summary>
    public static void RollDay(ActuatorEntity actuator, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (actuator.CounterDay == today) return;
        actuator.CounterDay = today;
        actuator.RunSecondsToday = 0;
    }

    /// <summary>A run by hand counts against the daily cap and clears a block: the person has looked at the actuator.</summary>
    public static void RecordManualRun(ActuatorEntity actuator, int seconds, DateTimeOffset now)
    {
        RollDay(actuator, now);
        actuator.LastRunAt = now;
        actuator.LastRunSeconds = seconds;
        actuator.RunningUntil = now.AddSeconds(seconds);
        actuator.RunSecondsToday += seconds;
        Reset(actuator);
    }

    /// <summary>The person acted on the actuator (a run, a new mode): it is not blocked any more and old suggestions are forgotten.</summary>
    public static void Reset(ActuatorEntity actuator)
    {
        actuator.State = ActuatorState.Idle;
        actuator.BlockedReason = null;
        actuator.FruitlessRuns = 0;
        actuator.LastRunStartValue = null;
        actuator.SuggestedAt = null;
        actuator.LastNoteCode = null;
    }

    public static ActuatorState StateOf(ActuatorEntity actuator, PotEntity pot, DateTimeOffset now) =>
        pot.KillSwitch || actuator.State == ActuatorState.Blocked ? ActuatorState.Blocked
        : actuator.RunningUntil > now ? ActuatorState.Running
        : ActuatorState.Idle;
}

/// <summary>
/// One look at one pot: tell the tier clamp to lower automation the plan no longer includes, read the readings, ask the rules what
/// each actuator should do, send the commands, and write down what happened and why.
/// </summary>
public sealed class AutomationCycle(GreenhausDbContext db, IReadingSource readings, ICommandSender commands, TierClamp clamp, IUserNotifier notifier,
    TimeProvider clock, ILogger<AutomationCycle> log)
{
    public async Task RunPotAsync(Guid potId, CancellationToken ct)
    {
        var pot = await db.Pots.FirstOrDefaultAsync(p => p.Id == potId, ct);
        if (pot is null) return;

        // A plan that lapsed without an event (a time-limited entitlement ran out) is caught here, before anything is decided.
        var clamped = await clamp.ClampAsync(pot.OwnerId, ct);
        if (clamped.Lowered.Count > 0)
        {
            // Nothing announced this drop, so the person is told here, once: the lowering is done and will not repeat.
            await notifier.NotifyAsync(pot.OwnerId, null, "plan_changed", new Dictionary<string, string>
            {
                ["from"] = (clamped.Previous ?? clamped.Tier).ToString().ToLowerInvariant(), ["to"] = clamped.Tier.ToString().ToLowerInvariant(),
                ["lowered"] = string.Join(", ", clamped.Lowered),
            }, ct);
        }

        var actuators = await db.Actuators.Where(a => a.PotId == potId && a.Mode != ControlMode.Off).OrderBy(a => a.Id).ToListAsync(ct);
        if (actuators.Count == 0 || pot.KillSwitch) return;

        var now = clock.GetUtcNow();
        var channels = await db.Channels.AsNoTracking().Where(c => c.PotId == potId && c.Active).ToListAsync(ct);
        var latest = await readings.LatestAsync(channels, ct);
        var snapshots = Snapshots(channels, latest);
        var targets = (await db.Requirements.AsNoTracking().Where(r => r.PotId == potId).ToListAsync(ct)).ToDictionary(r => r.Metric, r => r.ToDraft());
        var cutoffs = (await db.Devices.AsNoTracking().Where(d => d.PotId == potId).ToListAsync(ct)).ToDictionary(d => d.DeviceId, d => d.ThermalCutoffDeclared);

        foreach (var actuator in actuators)
        {
            ActuatorBook.RollDay(actuator, now);
            if (actuator.RunningUntil is { } until && until <= now) actuator.RunningUntil = null;

            var metric = ActuatorPolicy.MetricFor(actuator.Kind);
            var sensors = channels.Where(c => c.Metric == metric).ToList();
            var snapshot = new ActuatorSnapshot(
                actuator.Kind, actuator.Mode,
                new SafetyLimits(actuator.MaxRunSeconds, actuator.MinIntervalSeconds, actuator.DailyCapSeconds, actuator.MaxFruitlessRuns),
                actuator.LastRunAt, actuator.LastRunSeconds, actuator.RunningUntil, actuator.RunSecondsToday, actuator.FruitlessRuns, actuator.LastRunStartValue,
                actuator.State == ActuatorState.Blocked, cutoffs.GetValueOrDefault(actuator.DeviceId),
                SensorsCalibrated: sensors.Count > 0 && sensors.All(c => c.CalibratedAt is not null),
                // Tank sensors are Pulse safety channels that Greenhaus does not link to a pot yet, so the tank is never known to be empty here.
                TankEmpty: false);

            var decision = AutomationRules.Decide(snapshot, pot.KillSwitch, targets, snapshots, now);
            await ApplyAsync(pot, actuator, decision, now, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyAsync(PotEntity pot, ActuatorEntity actuator, AutomationDecision decision, DateTimeOffset now, CancellationToken ct)
    {
        switch (decision.Action)
        {
            case AutomationAction.Run:
            {
                Guid commandId;
                // Pulse being down is not the actuator's fault: nothing is counted, and the next cycle tries again.
                try { commandId = await commands.RunAsync(actuator.DeviceId, actuator.Id, decision.Seconds, ct); }
                catch (PulseException e) { log.LogWarning("The {Actuator} of pot {Pot} was not started: {Code}", actuator.Id, pot.Id, e.Code); return; }
                actuator.LastRunAt = now;
                actuator.LastRunSeconds = decision.Seconds;
                actuator.RunningUntil = now.AddSeconds(decision.Seconds);
                actuator.RunSecondsToday += decision.Seconds;
                actuator.FruitlessRuns = decision.FruitlessRuns;
                actuator.LastRunStartValue = decision.StartValue;
                actuator.SuggestedAt = null;
                actuator.LastNoteCode = null;
                db.AddAudit(pot.Id, decision.Actor, "run", decision.Reason, actuator.Id, null, clock, commandId);
                break;
            }
            case AutomationAction.Stop:
            {
                try { await commands.StopAsync(actuator.DeviceId, actuator.Id, ct); }
                catch (PulseException e) { log.LogWarning("The {Actuator} of pot {Pot} was not stopped: {Code}", actuator.Id, pot.Id, e.Code); return; }
                actuator.RunningUntil = null;
                actuator.LastNoteCode = null;
                db.AddAudit(pot.Id, decision.Actor, "stop", decision.Reason, actuator.Id, null, clock);
                break;
            }
            case AutomationAction.Suggest:
                actuator.LastNoteCode = null;
                // Once per episode: the person is not told every thirty seconds that the soil is still dry.
                if (actuator.SuggestedAt is null)
                {
                    actuator.SuggestedAt = now;
                    db.AddAudit(pot.Id, decision.Actor, "suggested", decision.Reason, actuator.Id, null, clock);
                    await Tell(pot, actuator, "automation_suggestion", decision, ct);
                }
                break;
            case AutomationAction.Block:
                actuator.State = ActuatorState.Blocked;
                actuator.BlockedReason = decision.Code;
                actuator.FruitlessRuns = decision.FruitlessRuns;
                actuator.RunningUntil = null;
                actuator.LastNoteCode = null;
                db.AddAudit(pot.Id, decision.Actor, "blocked", decision.Reason, actuator.Id, null, clock);
                await Tell(pot, actuator, "actuator_blocked", decision, ct);
                break;
            default:
                if (decision.Record)
                {
                    // A lasting condition (a silent sensor, an used-up cap) is written down and told once, not every cycle.
                    if (actuator.LastNoteCode != decision.Code)
                    {
                        actuator.LastNoteCode = decision.Code;
                        // Written down once. Only a suggestion and a block are notifications, a silent sensor is already told as a device going offline.
                        db.AddAudit(pot.Id, decision.Actor, "skipped", decision.Reason, actuator.Id, null, clock);
                    }
                }
                else
                {
                    actuator.LastNoteCode = null;
                    if (!decision.Demand) actuator.SuggestedAt = null;
                }
                break;
        }
    }

    /// <summary>Only the kind and its facts travel, the app writes the words (ADR 10).</summary>
    private Task Tell(PotEntity pot, ActuatorEntity actuator, string kind, AutomationDecision decision, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["pot"] = pot.Name, ["deviceId"] = actuator.DeviceId, ["actuatorId"] = actuator.Id, ["actuatorKind"] = actuator.Kind.ToString().ToLowerInvariant(), ["reason"] = decision.Code,
        };
        if (decision.Metric is { } metric) parameters["metric"] = MetricNames.Key(metric);
        if (decision.Value is { } value) parameters["value"] = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (decision.Limit is { } limit) parameters["target"] = limit.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (decision.Direction is { } direction) parameters["direction"] = direction;
        return notifier.NotifyAsync(pot.OwnerId, pot.Id, kind, parameters, ct);
    }
    /// <summary>The readings of each metric across the pot's active channels. A channel that has not reported makes its metric stale.</summary>
    private static Dictionary<Metric, MetricSnapshot> Snapshots(IReadOnlyCollection<ChannelEntity> channels, IReadOnlyList<ChannelReading> latest)
    {
        var byChannel = latest.ToDictionary(r => (r.DeviceId, r.ChannelId));
        return channels.GroupBy(c => c.Metric).ToDictionary(g => g.Key, g =>
        {
            var found = g.Select(c => byChannel.TryGetValue((c.DeviceId, c.ChannelId), out var r) ? r : null).ToList();
            var values = found.Where(r => r is not null).Select(r => r!.Value).ToList();
            var oldest = found.Any(r => r is null) ? DateTimeOffset.MinValue : found.Min(r => r!.At);
            return new MetricSnapshot(values, oldest);
        });
    }
}

/// <summary>
/// Runs a cycle for every pot with an actuator that is not off. Each pot is handled in its own transaction under a PostgreSQL
/// advisory lock, so several API instances never decide for the same pot at once. A pot that fails is logged and left for the next round.
/// </summary>
public sealed class AutomationRunner(IServiceScopeFactory scopes, ILogger<AutomationRunner> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        List<Guid> potIds;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GreenhausDbContext>();
            potIds = await db.Actuators.AsNoTracking().Where(a => a.Mode != ControlMode.Off).Select(a => a.PotId).Distinct().ToListAsync(ct);
        }

        var handled = 0;
        foreach (var potId in potIds)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GreenhausDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (!await TryLockAsync(db, potId, ct)) continue;
                await scope.ServiceProvider.GetRequiredService<AutomationCycle>().RunPotAsync(potId, ct);
                await tx.CommitAsync(ct);
                handled++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { log.LogError(e, "The control cycle of pot {Pot} failed, it is tried again next round", potId); }
        }
        return handled;
    }

    /// <summary>Held until the transaction ends. False means another instance has the pot.</summary>
    private static Task<bool> TryLockAsync(GreenhausDbContext db, Guid potId, CancellationToken ct)
    {
        var key = BitConverter.ToInt64(potId.ToByteArray(), 0);
        return db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").SingleAsync(ct);
    }
}

public sealed class AutomationLoop(AutomationRunner runner, IOptions<AutomationOptions> options, ILogger<AutomationLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try { await runner.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "The control loop failed, it runs again next round"); }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
