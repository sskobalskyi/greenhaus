using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Billing.Persistence;

public class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public const string Schema = "billing";

    public DbSet<SubscriptionEntity> Subscriptions => Set<SubscriptionEntity>();
    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();
    public DbSet<GrantEntity> Grants => Set<GrantEntity>();
    public DbSet<EntitlementStateEntity> EntitlementStates => Set<EntitlementStateEntity>();
    public DbSet<OutboxMessageEntity> Outbox => Set<OutboxMessageEntity>();
    public DbSet<InboxMessageEntity> Inbox => Set<InboxMessageEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);

        model.Entity<SubscriptionEntity>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.UserId);
            e.HasIndex(x => x.CurrentPeriodEnd);
            e.Property(x => x.Tier).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.PaymentToken).HasMaxLength(200);
        });

        model.Entity<InvoiceEntity>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.IssuedAt });
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasMaxLength(16);
        });

        model.Entity<EntitlementStateEntity>(e =>
        {
            e.ToTable("entitlement_states");
            e.HasKey(x => x.UserId);
            e.Property(x => x.Tier).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
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

        model.Entity<GrantEntity>(e =>
        {
            e.ToTable("grants");
            e.HasKey(x => x.Id);
            // One grant per order: this is what makes order events idempotent.
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Tier).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
        });
    }
}