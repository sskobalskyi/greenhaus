using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Fulfilment.Persistence;

public class FulfilmentDbContext(DbContextOptions<FulfilmentDbContext> options) : DbContext(options)
{
    public const string Schema = "fulfilment";

    public DbSet<AllocationEntity> Allocations => Set<AllocationEntity>();
    public DbSet<OutboxMessageEntity> Outbox => Set<OutboxMessageEntity>();
    public DbSet<InboxMessageEntity> Inbox => Set<InboxMessageEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);

        model.Entity<AllocationEntity>(e =>
        {
            e.ToTable("allocations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrderId, x.Sku, x.Index }).IsUnique();
            e.HasIndex(x => x.DeviceId).IsUnique();
            e.HasIndex(x => new { x.State, x.NextAttemptAt });
            e.Property(x => x.CustomerId).HasMaxLength(100);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.DeviceId).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.State).HasMaxLength(24);
            e.Property(x => x.Intent).HasMaxLength(8);
            e.Property(x => x.LastError).HasMaxLength(300);
            e.Property(x => x.Capabilities).HasColumnType("jsonb");
        });

        model.Entity<OutboxMessageEntity>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PublishedAt);
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Traceparent).HasMaxLength(64);
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });

        model.Entity<InboxMessageEntity>(e =>
        {
            e.ToTable("inbox");
            e.HasKey(x => new { x.MessageId, x.Consumer });
            e.Property(x => x.Consumer).HasMaxLength(64);
        });
    }
}