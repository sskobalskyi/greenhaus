using System.Threading.RateLimiting;
using Greenhaus.Api.Auth;
using Greenhaus.Api.Contracts;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Messaging;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

public static class ClaimEndpoints
{
    public const string Policy = "claims";

    public static void MapClaims(this RouteGroupBuilder api)
    {
        api.MapGet("/claims/preview", async (string? deviceId, string? token, IPulseClient pulse, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(token)) return Problems.Invalid("A device and a token are required");
            try
            {
                var device = await pulse.GetDeviceAsync(deviceId, ct);
                if (device is null || device.State == "retired") return Problems.NotFound("Unknown device");
                var claimable = device.State == "shipped";
                // The token is not checked here, that would spend it. A wrong token is reported when claiming.
                return Results.Ok(new ClaimPreviewDto
                {
                    DeviceId = device.Id, Model = device.Model, Claimable = claimable,
                    Reason = claimable ? null : device.State == "allocated" ? "not_shipped" : "already_claimed",
                    Channels = [.. Known(device).Select(c => new ClaimChannelDto { ChannelId = c.Id, Metric = Parse(c.Metric) })],
                    Actuators = [.. device.Actuators.Select(a => new ClaimActuatorDto { Id = a.Id, Kind = Enum.Parse<ActuatorKind>(a.Kind, ignoreCase: true) })],
                });
            }
            catch (PulseException e) { return PulseProblems.From(e); }
        }).RequireRateLimiting(Policy);

        api.MapPost("/claims", Claim).RequireRateLimiting(Policy);
    }

    private static async Task<IResult> Claim(ClaimRequest body, HttpContext http, GreenhausDbContext db, IPulseClient pulse, DeviceSync sync, EventPublisher events, TimeProvider clock, CancellationToken ct)
    {
        var user = http.User.UserId();
        if (string.IsNullOrWhiteSpace(body.DeviceId) || string.IsNullOrWhiteSpace(body.Token) || body.PotId is null)
            return Problems.Invalid("A device, a token and a pot are required");
        if (await db.OwnedPotAsync(body.PotId.Value, user, ct) is not { } pot) return Problems.NotFound("Unknown pot");
        if (await db.Devices.AnyAsync(d => d.DeviceId == body.DeviceId, ct)) return Problems.Fail(409, "already_claimed", "This device is already linked to a pot");

        PulseDevice device;
        try { device = await pulse.ClaimAsync(body.DeviceId, body.Token, user, ct); }
        catch (PulseException e) { return PulseProblems.From(e); }

        var zones = await db.Zones.Where(z => z.PotId == pot.Id).OrderBy(z => z.Row).ThenBy(z => z.Col).ToListAsync(ct);
        var now = clock.GetUtcNow();

        db.Devices.Add(new LinkedDeviceEntity
        {
            DeviceId = device.Id, PotId = pot.Id, OwnerId = user, Model = device.Model, LinkedAt = now,
            ThermalCutoffDeclared = device.Actuators.Any(a => a.ThermalCutoff == true),
        });

        // Simple view shows one sensor per metric, so extra channels of a metric are kept but hidden. The zone grid spreads them in order.
        var seen = new Dictionary<Metric, int>(await db.Channels.Where(c => c.PotId == pot.Id && c.Active).GroupBy(c => c.Metric).ToDictionaryAsync(g => g.Key, g => g.Count(), ct));
        foreach (var channel in Known(device))
        {
            var metric = Parse(channel.Metric);
            var index = seen.GetValueOrDefault(metric);
            seen[metric] = index + 1;
            var simple = pot.ViewMode == ViewMode.Simple;
            db.Channels.Add(new ChannelEntity
            {
                PotId = pot.Id, DeviceId = device.Id, ChannelId = channel.Id, Metric = metric, Unit = channel.Unit,
                ZoneId = simple ? zones[0].Id : zones[index % zones.Count].Id,
                Active = !simple || index == 0,
            });
        }

        foreach (var a in device.Actuators)
        {
            var kind = Enum.Parse<ActuatorKind>(a.Kind, ignoreCase: true);
            var hard = new SafetyLimits(a.HardLimits.MaxRunSeconds, a.HardLimits.MinIntervalSeconds, a.HardLimits.DailyCapSeconds);
            var limits = ActuatorPolicy.DefaultLimits(kind, hard);
            db.Actuators.Add(new ActuatorEntity
            {
                PotId = pot.Id, Id = a.Id, DeviceId = device.Id, Kind = kind, Mode = ControlMode.Off,
                MaxRunSeconds = limits.MaxRunSeconds, MinIntervalSeconds = limits.MinIntervalSeconds, DailyCapSeconds = limits.DailyCapSeconds, MaxFruitlessRuns = limits.MaxFruitlessRuns,
                HardMaxRunSeconds = hard.MaxRunSeconds, HardMinIntervalSeconds = hard.MinIntervalSeconds, HardDailyCapSeconds = hard.DailyCapSeconds,
            });
        }

        // The order is known when Orderflow allocated this device. A do-it-yourself build has none, and the event says so by omitting it.
        var orderId = await db.DeviceOrders.AsNoTracking().Where(o => o.DeviceId == device.Id).Select(o => (Guid?)o.OrderId).FirstOrDefaultAsync(ct);
        events.Stage("device.claimed.v1", new { deviceId = device.Id, ownerId = user, potId = pot.Id, orderId, claimedAt = now });
        GreenhausTelemetry.DevicesClaimed.Add(1);
        db.AddAudit(pot.Id, "user", "device_claimed", $"Controller {device.Id} linked", null, null, clock);
        await db.SaveChangesAsync(ct);
        await sync.SyncRulesAsync(pot.Id, ct);

        return Results.Created($"/api/greenhaus/v1/pots/{pot.Id}/devices", new ClaimDto { DeviceId = device.Id, PotId = pot.Id, ClaimedAt = now });
    }

    /// <summary>Safety sensors (tank level, overflow, flow) are Pulse metrics Greenhaus does not show as zone readings.</summary>
    private static IEnumerable<PulseChannel> Known(PulseDevice device) => device.Channels.Where(c => MetricNames.TryParse(c.Metric, out _));

    private static Metric Parse(string key) => MetricNames.TryParse(key, out var metric) ? metric : throw new InvalidOperationException(key);

    public static void AddClaimRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy(Policy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.IsAuthenticated == true ? http.User.UserId() : http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
            o.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await Problems.Fail(429, "rate_limited", "Too many attempts, try again shortly").ExecuteAsync(context.HttpContext);
            };
        });
}