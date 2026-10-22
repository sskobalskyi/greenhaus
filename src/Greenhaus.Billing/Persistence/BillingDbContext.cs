using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Billing.Persistence;

public class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public const string Schema = "billing";

    public DbSet<SubscriptionEntity> Subscriptions => Set<SubscriptionEntity>();
    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();
    public DbSet<GrantEntity> Grants => Set<GrantEntity>();

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