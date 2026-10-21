using Greenhaus.Api.Contracts;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Features;

public static class Mapping
{
    public static PotDto ToDto(this PotEntity p) => new()
    {
        Id = p.Id, Name = p.Name, Location = p.Location, Kind = p.Kind, ViewMode = p.ViewMode,
        GridRows = p.GridRows, GridCols = p.GridCols, KillSwitch = p.KillSwitch, CreatedAt = p.CreatedAt,
    };

    public static ZoneDto ToDto(this ZoneEntity z) => new() { Id = z.Id, Row = z.Row, Col = z.Col, Label = z.Label };

    public static PlantDto ToDto(this PlantEntity p) => new()
    {
        Id = p.Id, SpeciesId = p.SpeciesId, Nickname = p.Nickname, ZoneId = p.ZoneId, PlantedAt = p.PlantedAt, Notes = p.Notes,
    };

    public static CareTaskDto ToDto(this CareTaskEntity t) => new()
    {
        Id = t.Id, Kind = t.Kind, Title = t.Title, DueAt = t.DueAt, State = t.State, CompletedAt = t.CompletedAt, Origin = t.Origin,
    };

    public static AuditEntryDto ToDto(this AuditEntity a) => new()
    {
        Id = a.Id, At = a.At, Actor = a.Actor, ActuatorId = a.ActuatorId, Action = a.Action, Reason = a.Reason, Outcome = a.Outcome,
    };

    public static MetricRequirementDto ToDto(this RequirementEntity r) => new() { Metric = r.Metric, Enabled = r.Enabled, Min = r.Min, Max = r.Max, Margin = r.Margin };

    public static RequirementDraft ToDraft(this RequirementEntity r) => new(r.Metric, r.Enabled, r.Min, r.Max, r.Margin);

    public static RequirementsDto ToDto(this IEnumerable<RequirementEntity> items) => new()
    {
        // Every metric is listed, in a stable order, so a client never has to guess what is missing.
        Metrics = [.. Enum.GetValues<Metric>().Select(m => items.FirstOrDefault(r => r.Metric == m)?.ToDto() ?? new MetricRequirementDto { Metric = m, Enabled = false })],
    };

    /// <summary>The pot the caller owns, or null. A pot that belongs to someone else does not exist for them.</summary>
    public static Task<PotEntity?> OwnedPotAsync(this GreenhausDbContext db, Guid id, string owner, CancellationToken ct) =>
        db.Pots.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == owner, ct);

    public static void AddAudit(this GreenhausDbContext db, Guid potId, string actor, string action, string reason, string? actuatorId, string? outcome, TimeProvider clock, Guid? commandId = null) =>
        db.Audit.Add(new AuditEntity
        {
            Id = Guid.NewGuid(), PotId = potId, At = clock.GetUtcNow(), Actor = actor, ActuatorId = actuatorId, Action = action, Reason = reason, Outcome = outcome, CommandId = commandId,
        });
    public static ZoneEntity NewZone(Guid potId, int row, int col) => new() { Id = Guid.NewGuid(), PotId = potId, Row = row, Col = col };

    public static List<ZoneEntity> BuildZones(Guid potId, int rows, int cols, bool simple)
    {
        if (simple) return [NewZone(potId, 0, 0)];
        var zones = new List<ZoneEntity>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++) zones.Add(NewZone(potId, r, c));
        return zones;
    }
}
