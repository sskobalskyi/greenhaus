using Greenhaus.Fulfilment.Auth;
using Greenhaus.Fulfilment.Contracts;
using Greenhaus.Fulfilment.Domain;
using Greenhaus.Fulfilment.Infrastructure;
using Greenhaus.Fulfilment.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Greenhaus.Fulfilment.Features;

public static class AllocationEndpoints
{
    public static void MapFulfilment(this RouteGroupBuilder api)
    {
        // Everything here belongs to the people who pack devices: it shows secrets that exist only until the box is sealed.
        api.AddEndpointFilter(async (context, next) =>
        {
            var role = context.HttpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.AdminRole;
            return context.HttpContext.User.IsFulfilmentAdmin(role)
                ? await next(context)
                : Problems.Fail(403, "forbidden", "Only fulfilment staff can see this");
        });

        api.MapGet("/orders/{orderId:guid}/allocations", async (Guid orderId, FulfilmentDbContext db, IOptions<WebOptions> web, CancellationToken ct) =>
        {
            var rows = await Load(db, orderId, ct);
            return rows.Count == 0 ? Problems.NotFound("No devices were allocated for this order") : Results.Ok(rows.Select(r => ToDto(r, web.Value)).ToList());
        });

        // A device Pulse never accepted waits here. Asking again puts it back in the queue of the worker.
        api.MapPost("/orders/{orderId:guid}/allocations/retry", async (Guid orderId, FulfilmentDbContext db, IOptions<WebOptions> web, TimeProvider clock, CancellationToken ct) =>
        {
            var rows = await Load(db, orderId, ct);
            if (rows.Count == 0) return Problems.NotFound("No devices were allocated for this order");
            foreach (var row in rows.Where(r => r.State == AllocationState.PulseFailed))
            {
                row.State = row.Intent == "retire" ? AllocationState.RetireRequested : AllocationState.ShipRequested;
                row.PulseAttempts = 0;
                row.NextAttemptAt = clock.GetUtcNow();
                row.LastError = null;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(rows.Select(r => ToDto(r, web.Value)).ToList());
        });
    }

    private static Task<List<AllocationEntity>> Load(FulfilmentDbContext db, Guid orderId, CancellationToken ct) =>
        db.Allocations.Where(a => a.OrderId == orderId).OrderBy(a => a.Sku).ThenBy(a => a.Index).ToListAsync(ct);

    private static AllocationDto ToDto(AllocationEntity a, WebOptions web)
    {
        var dto = new AllocationDto
        {
            DeviceId = a.DeviceId, OrderId = a.OrderId, Sku = a.Sku, Model = a.Model, State = a.State,
            AllocatedAt = a.AllocatedAt, ClaimTokenExpiresAt = a.ClaimTokenExpiresAt, ShippedAt = a.ShippedAt, LastError = a.LastError,
        };
        // The secrets are shown for a device in the packing room, and for no other.
        if (a.State == AllocationState.Allocated && a.ClaimToken is not null)
        {
            dto.ClaimToken = a.ClaimToken;
            dto.ClaimUrl = $"{web.BaseUrl.TrimEnd('/')}/claim?d={Uri.EscapeDataString(a.DeviceId)}&t={Uri.EscapeDataString(a.ClaimToken)}";
            dto.Credential = a.Credential;
        }
        return dto;
    }
}