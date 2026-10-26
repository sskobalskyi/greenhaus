using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Persistence;

public class GreenhausDbContext(DbContextOptions<GreenhausDbContext> options) : DbContext(options)
{
    public const string Schema = "greenhaus";

    public DbSet<UserProfileEntity> Profiles => Set<UserProfileEntity>();
    public DbSet<EntitlementEntity> Entitlements => Set<EntitlementEntity>();
    public DbSet<PotEntity> Pots => Set<PotEntity>();
    public DbSet<ZoneEntity> Zones => Set<ZoneEntity>();
    public DbSet<PlantEntity> Plants => Set<PlantEntity>();
    public DbSet<RequirementEntity> Requirements => Set<RequirementEntity>();
    public DbSet<LinkedDeviceEntity> Devices => Set<LinkedDeviceEntity>();
    public DbSet<ChannelEntity> Channels => Set<ChannelEntity>();
    public DbSet<ActuatorEntity> Actuators => Set<ActuatorEntity>();
    public DbSet<AuditEntity> Audit => Set<AuditEntity>();
    public DbSet<CareTaskEntity> Tasks => Set<CareTaskEntity>();
    public DbSet<OutboxMessageEntity> Outbox => Set<OutboxMessageEntity>();
    public DbSet<InboxMessageEntity> Inbox => Set<InboxMessageEntity>();
    public DbSet<AssistantUsageEntity> AssistantUsage => Set<AssistantUsageEntity>();
    public DbSet<DeviceOrderEntity> DeviceOrders => Set<DeviceOrderEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);

        model.Entity<UserProfileEntity>(e =>
        {
            e.ToTable("profiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(60);
            e.Property(x => x.Locale).HasMaxLength(8);
        });

        model.Entity<EntitlementEntity>(e =>
        {
            e.ToTable("entitlements");
            e.HasKey(x => x.UserId);
            e.Property(x => x.Tier).HasConversion<string>().HasMaxLength(16);
        });

        model.Entity<PotEntity>(e =>
        {
            e.ToTable("pots");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerId);
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Location).HasMaxLength(60);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ViewMode).HasConversion<string>().HasMaxLength(16);
            e.HasMany(x => x.Zones).WithOne().HasForeignKey(x => x.PotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Plants).WithOne().HasForeignKey(x => x.PotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Requirements).WithOne().HasForeignKey(x => x.PotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Devices).WithOne().HasForeignKey(x => x.PotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Channels).WithOne().HasForeignKey(x => x.PotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Actuators).WithOne().HasForeignKey(x => x.PotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Audit).WithOne().HasForeignKey(x => x.PotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.PotId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ZoneEntity>(e =>
        {
            e.ToTable("zones");
            e.HasKey(x => x.Id);
            e.Property(x => x.Label).HasMaxLength(40);
        });

        model.Entity<PlantEntity>(e =>
        {
            e.ToTable("plants");
            e.HasKey(x => x.Id);
            e.Property(x => x.SpeciesId).HasMaxLength(64);
            e.Property(x => x.Nickname).HasMaxLength(60);
            e.Property(x => x.Notes).HasMaxLength(500);
        });

        model.Entity<RequirementEntity>(e =>
        {
            e.ToTable("requirements");
            e.HasKey(x => new { x.PotId, x.Metric });
            e.Property(x => x.Metric).HasConversion<string>().HasMaxLength(24);
        });

        model.Entity<LinkedDeviceEntity>(e =>
        {
            e.ToTable("linked_devices");
            // A device belongs to one pot at a time, and the id is the one Pulse knows it by.
            e.HasKey(x => x.DeviceId);
            e.HasIndex(x => x.OwnerId);
            e.Property(x => x.DeviceId).HasMaxLength(64);
        });

        model.Entity<ChannelEntity>(e =>
        {
            e.ToTable("channels");
            e.HasKey(x => new { x.DeviceId, x.ChannelId });
            e.HasIndex(x => x.PotId);
            e.Property(x => x.Metric).HasConversion<string>().HasMaxLength(24);
        });

        model.Entity<ActuatorEntity>(e =>
        {
            e.ToTable("actuators");
            e.HasKey(x => new { x.PotId, x.Id });
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.BlockedReason).HasMaxLength(40);
            e.Property(x => x.LastNoteCode).HasMaxLength(40);
        });

        model.Entity<AuditEntity>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PotId, x.At });
            e.HasIndex(x => x.CommandId);
        });

        model.Entity<OutboxMessageEntity>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PublishedAt);
            e.Property(x => x.Traceparent).HasMaxLength(64);
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });

        model.Entity<AssistantUsageEntity>(e =>
        {
            e.ToTable("assistant_usage");
            e.HasKey(x => new { x.UserId, x.Month });
            e.Property(x => x.UserId).HasMaxLength(64);
        });

        model.Entity<InboxMessageEntity>(e =>
        {
            e.ToTable("inbox");
            e.HasKey(x => new { x.MessageId, x.Consumer });
            e.Property(x => x.Consumer).HasMaxLength(64);
        });

        model.Entity<DeviceOrderEntity>(e =>
        {
            e.ToTable("device_orders");
            e.HasKey(x => x.DeviceId);
            e.Property(x => x.DeviceId).HasMaxLength(64);
        });

        model.Entity<NotificationEntity>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.ParamsJson).HasColumnType("jsonb");
            e.Property(x => x.DedupeKey).HasMaxLength(200);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.DedupeKey }).IsUnique().HasFilter("\"DedupeKey\" IS NOT NULL");
        });

        model.Entity<CareTaskEntity>(e =>
        {
            e.ToTable("care_tasks");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PotId, x.State });
            e.Property(x => x.Title).HasMaxLength(80);
        });
    }
}
