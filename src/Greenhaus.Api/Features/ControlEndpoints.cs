using Greenhaus.Api.Auth;
using Greenhaus.Api.Automation;
using Greenhaus.Api.Contracts;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

public static class ControlEndpoints
{
    public static void MapControls(this RouteGroupBuilder api)
    {
        api.MapGet("/pots/{potId:guid}/status", Status);

        api.MapGet("/pots/{potId:guid}/devices", async (Guid potId, HttpContext http, GreenhausDbContext db, IPulseClient pulse, ILogger<PulseReadingSource> log, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var devices = await db.Devices.AsNoTracking().Where(d => d.PotId == potId).OrderBy(d => d.LinkedAt).ToListAsync(ct);
            var result = await Task.WhenAll(devices.Select(async d =>
            {
                PulseDevice? live = null;
                try { live = await pulse.GetDeviceAsync(d.DeviceId, ct); }
                catch (PulseException e) { log.LogWarning("Could not read {Device} from Pulse: {Code}", d.DeviceId, e.Code); }
                // A device Pulse cannot describe right now is shown offline, never as healthy.
                return new LinkedDeviceDto
                {
                    DeviceId = d.DeviceId, Model = d.Model, Status = live?.Connection == "online" ? "online" : "offline",
                    LastSeenAt = live?.LastSeenAt, Firmware = live?.Firmware, ThermalCutoffDeclared = d.ThermalCutoffDeclared,
                };
            }));
            return Results.Ok(result.ToList());
        });

        api.MapGet("/pots/{potId:guid}/actuators", async (Guid potId, HttpContext http, GreenhausDbContext db, EntitlementService entitlements, TimeProvider clock, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            var pot = await db.OwnedPotAsync(potId, user, ct);
            if (pot is null) return Problems.NotFound();
            var tier = await entitlements.TierOfAsync(user, ct);
            var actuators = await db.Actuators.AsNoTracking().Where(a => a.PotId == potId).OrderBy(a => a.Id).ToListAsync(ct);
            return Results.Ok(actuators.Select(a => ToDto(a, pot, tier, clock.GetUtcNow())).ToList());
        });

        api.MapPut("/pots/{potId:guid}/actuators/{actuatorId}/mode", async (Guid potId, string actuatorId, ModeUpdate body, HttpContext http, GreenhausDbContext db, EntitlementService entitlements, EventPublisher events, TimeProvider clock, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            var pot = await db.OwnedPotAsync(potId, user, ct);
            if (pot is null) return Problems.NotFound();
            var actuator = await db.Actuators.FirstOrDefaultAsync(a => a.PotId == potId && a.Id == actuatorId, ct);
            if (actuator is null) return Problems.NotFound("Unknown actuator");
            if (body.Mode is null) return Problems.Invalid("A mode is required");

            var tier = await entitlements.TierOfAsync(user, ct);
            var cutoff = await db.Devices.AnyAsync(d => d.DeviceId == actuator.DeviceId && d.ThermalCutoffDeclared, ct);
            var metric = ActuatorPolicy.MetricFor(actuator.Kind);
            var sensors = await db.Channels.Where(c => c.PotId == potId && c.Active && c.Metric == metric).ToListAsync(ct);
            var calibrated = sensors.Count > 0 && sensors.All(c => c.CalibratedAt is not null);

            switch (ActuatorPolicy.CheckMode(actuator.Kind, body.Mode.Value, tier, cutoff, calibrated))
            {
                case ModeCheck.TierRequired:
                    await events.LimitReachedAsync(user, "automation", tier, ct);
                    return Problems.Fail(403, "tier_required", "This automation needs a higher tier", null, "automation", TierPolicy.LowestTierAllowingAuto(metric));
                case ModeCheck.ThermalCutoffRequired:
                    return Problems.Fail(400, "thermal_cutoff_required", "A heater needs a declared thermal cutoff before it can run automatically");
                case ModeCheck.CalibrationRequired:
                    return Problems.Fail(400, "calibration_required", "Calibrate the soil moisture sensor before automatic watering");
            }

            var before = actuator.Mode;
            actuator.Mode = body.Mode.Value;
            // The person looked at the actuator: a block from the safety stop ends and old suggestions are forgotten.
            ActuatorBook.Reset(actuator);
            db.AddAudit(potId, "user", "mode_changed", $"{before} -> {actuator.Mode}".ToLowerInvariant(), actuator.Id, null, clock);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(actuator, pot, tier, clock.GetUtcNow()));
        });

        api.MapPost("/pots/{potId:guid}/actuators/{actuatorId}/run", async (Guid potId, string actuatorId, RunRequest body, HttpContext http, GreenhausDbContext db, ICommandSender commands, TimeProvider clock, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            var pot = await db.OwnedPotAsync(potId, user, ct);
            if (pot is null) return Problems.NotFound();
            var actuator = await db.Actuators.FirstOrDefaultAsync(a => a.PotId == potId && a.Id == actuatorId, ct);
            if (actuator is null) return Problems.NotFound("Unknown actuator");
            if (body.DurationSeconds is not (>= 1 and <= 3600)) return Problems.Invalid("Duration must be 1 to 3600 seconds");
            if (pot.KillSwitch) return Problems.Fail(409, "kill_switch_on", "The kill switch is on", "Turn it off to run an actuator");

            var seconds = Math.Min(body.DurationSeconds.Value, actuator.MaxRunSeconds);
            Guid commandId;
            try { commandId = await commands.RunAsync(actuator.DeviceId, actuator.Id, seconds, ct); }
            catch (PulseException e) { return PulseProblems.From(e); }
            ActuatorBook.RecordManualRun(actuator, seconds, clock.GetUtcNow());
            db.AddAudit(potId, "user", "run", $"{seconds} s requested by the user", actuator.Id, null, clock, commandId);
            await db.SaveChangesAsync(ct);
            return Results.Json(new CommandReceiptDto { CommandId = commandId, Status = "sent" }, statusCode: 202);
        });

        api.MapPut("/pots/{potId:guid}/actuators/{actuatorId}/limits", async (Guid potId, string actuatorId, SafetyLimitsDto body, HttpContext http, GreenhausDbContext db, EntitlementService entitlements, TimeProvider clock, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            var pot = await db.OwnedPotAsync(potId, user, ct);
            if (pot is null) return Problems.NotFound();
            var actuator = await db.Actuators.FirstOrDefaultAsync(a => a.PotId == potId && a.Id == actuatorId, ct);
            if (actuator is null) return Problems.NotFound("Unknown actuator");

            var hard = new SafetyLimits(actuator.HardMaxRunSeconds, actuator.HardMinIntervalSeconds, actuator.HardDailyCapSeconds);
            if (!ActuatorPolicy.WithinHardLimits(body.ToDomain(), hard))
                return Problems.Fail(400, "limits_beyond_hard", "Limits can only be tightened within the controller's hard limits");
            actuator.MaxRunSeconds = body.MaxRunSeconds;
            actuator.MinIntervalSeconds = body.MinIntervalSeconds;
            actuator.DailyCapSeconds = body.DailyCapSeconds;
            actuator.MaxFruitlessRuns = body.MaxFruitlessRuns;
            db.AddAudit(potId, "user", "limit_changed", "Safety limits tightened", actuator.Id, null, clock);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(actuator, pot, await entitlements.TierOfAsync(user, ct), clock.GetUtcNow()));
        });

        api.MapPut("/pots/{potId:guid}/kill-switch", async (Guid potId, KillSwitchUpdate body, HttpContext http, GreenhausDbContext db, DeviceSync sync, TimeProvider clock, CancellationToken ct) =>
        {
            var user = http.User.UserId();
            var pot = await db.OwnedPotAsync(potId, user, ct);
            if (pot is null) return Problems.NotFound();
            if (body.Enabled is null) return Problems.Invalid("enabled is required");
            if (pot.KillSwitch != body.Enabled)
            {
                pot.KillSwitch = body.Enabled.Value;
                db.AddAudit(potId, "user", "kill_switch", pot.KillSwitch ? "All automation stopped by the user" : "Automation resumed by the user", null, null, clock);
                await db.SaveChangesAsync(ct);
                await sync.SetKillSwitchAsync(potId, pot.KillSwitch, ct);
            }
            return Results.Ok(pot.ToDto());
        });

        api.MapGet("/pots/{potId:guid}/audit-log", async (Guid potId, int? limit, HttpContext http, GreenhausDbContext db, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            if (limit is < 1 or > 200) return Problems.Invalid("limit must be 1 to 200");
            var entries = await db.Audit.AsNoTracking().Where(a => a.PotId == potId).OrderByDescending(a => a.At).Take(limit ?? 50).ToListAsync(ct);
            return Results.Ok(entries.Select(a => a.ToDto()).ToList());
        });

        api.MapPut("/pots/{potId:guid}/channels/{channelId}/calibration", async (Guid potId, string channelId, CalibrationDto body, HttpContext http, GreenhausDbContext db, IPulseClient pulse, TimeProvider clock, CancellationToken ct) =>
        {
            if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
            var channel = await db.Channels.FirstOrDefaultAsync(c => c.PotId == potId && c.ChannelId == channelId && c.Active, ct);
            if (channel is null) return Problems.NotFound("Unknown channel");
            if (body.DryRaw == body.WetRaw) return Problems.Invalid("Dry and wet raw values must differ");
            try { await pulse.CalibrateAsync(channel.DeviceId, channel.ChannelId, body.DryRaw, body.WetRaw, ct); }
            catch (PulseException e) { return PulseProblems.From(e); }
            channel.DryRaw = body.DryRaw;
            channel.WetRaw = body.WetRaw;
            channel.CalibratedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new CalibrationDto { DryRaw = body.DryRaw, WetRaw = body.WetRaw, CalibratedAt = channel.CalibratedAt });
        });
    }

    private static ActuatorDto ToDto(ActuatorEntity a, PotEntity pot, Tier tier, DateTimeOffset now)
    {
        var metric = ActuatorPolicy.MetricFor(a.Kind);
        return new ActuatorDto
        {
            Id = a.Id, Kind = a.Kind, Metric = metric, Mode = a.Mode,
            State = ActuatorBook.StateOf(a, pot, now),
            MaxModeForTier = TierPolicy.LimitsFor(tier).MaxModeFor(metric),
            Limits = new SafetyLimitsDto { MaxRunSeconds = a.MaxRunSeconds, MinIntervalSeconds = a.MinIntervalSeconds, DailyCapSeconds = a.DailyCapSeconds, MaxFruitlessRuns = a.MaxFruitlessRuns },
            HardLimits = new SafetyLimitsDto { MaxRunSeconds = a.HardMaxRunSeconds, MinIntervalSeconds = a.HardMinIntervalSeconds, DailyCapSeconds = a.HardDailyCapSeconds },
            LastRunAt = a.LastRunAt,
        };
    }

    private static async Task<IResult> Status(Guid potId, HttpContext http, GreenhausDbContext db, IReadingSource readings, TimeProvider clock, CancellationToken ct)
    {
        if (await db.OwnedPotAsync(potId, http.User.UserId(), ct) is null) return Problems.NotFound();
        return Results.Ok(await StatusBuilder.BuildAsync(db, readings, clock, potId, ct));
    }
}