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

        api.MapGet("/orders/{orderId:guid}/allocations", async (Guid orderId, FulfilmentDbContext db, IOptions<WebOptions> web, SecretBox secrets, ILogger<AllocationEntity> log, CancellationToken ct) =>
        {
            var rows = await Load(db, orderId, ct);
            return rows.Count == 0 ? Problems.NotFound("No devices were allocated for this order") : Present(rows, web.Value, secrets, log);
        });

        // A device Pulse never accepted waits here. Asking again puts it back in the queue of the worker.
        api.MapPost("/orders/{orderId:guid}/allocations/retry", async (Guid orderId, FulfilmentDbContext db, IOptions<WebOptions> web, SecretBox secrets, ILogger<AllocationEntity> log, TimeProvider clock, CancellationToken ct) =>
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
            return Present(rows, web.Value, secrets, log);
        });
    }

    private static Task<List<AllocationEntity>> Load(FulfilmentDbContext db, Guid orderId, CancellationToken ct) =>
        db.Allocations.Where(a => a.OrderId == orderId).OrderBy(a => a.Sku).ThenBy(a => a.Index).ToListAsync(ct);

    /// <summary>
    /// The allocations as the packing room sees them. A secret that does not open (a wrong key, or a value that was changed or moved
    /// in the database) is a refusal with its own code: the secret is never guessed at, and the sealed text is never handed out.
    /// </summary>
    private static IResult Present(List<AllocationEntity> rows, WebOptions web, SecretBox secrets, ILogger log)
    {
        var dtos = new List<AllocationDto>();
        foreach (var row in rows)
        {
            if (!TryToDto(row, web, secrets, out var dto))
            {
                log.LogError("The packing secrets of {Device} cannot be opened: the key is wrong or the stored value was changed", row.DeviceId);
                return Problems.Fail(500, "secrets_unreadable", "The packing secrets of a device cannot be read", "Check Fulfilment:SecretsKey and the stored values; the device stays allocated.");
            }
            dtos.Add(dto);
        }
        return Results.Ok(dtos);
    }

    private static bool TryToDto(AllocationEntity a, WebOptions web, SecretBox secrets, out AllocationDto dto)
    {
        dto = new AllocationDto
        {
            DeviceId = a.DeviceId, OrderId = a.OrderId, Sku = a.Sku, Model = a.Model, State = a.State,
            AllocatedAt = a.AllocatedAt, ClaimTokenExpiresAt = a.ClaimTokenExpiresAt, ShippedAt = a.ShippedAt, LastError = a.LastError,
        };
        // The secrets are shown for a device in the packing room, and for no other.
        if (a.State == AllocationState.Allocated && a.ClaimToken is not null)
        {
            if (!secrets.TryOpen(a.ClaimToken, SecretBox.ClaimTokenField, a.Id, a.DeviceId, out var token) || token is null) return false;
            string? credential = null;
            if (a.Credential is not null && (!secrets.TryOpen(a.Credential, SecretBox.CredentialField, a.Id, a.DeviceId, out credential) || credential is null)) return false;
            dto.ClaimToken = token;
            dto.ClaimUrl = $"{web.BaseUrl.TrimEnd('/')}/claim?d={Uri.EscapeDataString(a.DeviceId)}&t={Uri.EscapeDataString(token)}";
            dto.Credential = credential;
        }
        return true;
    }
}