using Microsoft.EntityFrameworkCore;

namespace LoanApp.Api.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<LoanApplication> Applications => Set<LoanApplication>();
    public DbSet<PolicyHead> PolicyHeads => Set<PolicyHead>();
    public DbSet<PolicyRevision> PolicyRevisions => Set<PolicyRevision>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var c = b.Entity<Customer>();
        c.ToTable("Customers", t => t.HasCheckConstraint("CK_Customer_Ssn", "\"Ssn\" ~ '^[0-9]{9}$'"));
        c.HasIndex(x => x.Ssn).IsUnique(); c.Property(x => x.Ssn).HasMaxLength(9);
        c.Property(x => x.FirstName).HasMaxLength(100); c.Property(x => x.LastName).HasMaxLength(100);
        c.Property(x => x.AddressLine1).HasMaxLength(200); c.Property(x => x.AddressLine2).HasMaxLength(200);
        c.Property(x => x.City).HasMaxLength(100); c.Property(x => x.State).HasMaxLength(2);
        c.Property(x => x.PostalCode).HasMaxLength(20); c.Property(x => x.CompanyName).HasMaxLength(200);
        var a = b.Entity<LoanApplication>();
        a.ToTable("Applications", t =>
        {
            t.HasCheckConstraint("CK_Application_Amount", "\"RequestedAmount\" > 0");
            t.HasCheckConstraint("CK_Application_Version", "\"Version\" >= 1");
            t.HasCheckConstraint("CK_Application_Currency", "\"Currency\" = 'USD'");
        });
        a.Property(x => x.RequestedAmount).HasPrecision(11, 2); a.Property(x => x.Currency).HasMaxLength(3);
        a.Property(x => x.Version).IsConcurrencyToken(); a.HasIndex(x => x.CustomerId).IsUnique();
        a.HasOne<Customer>().WithOne().HasForeignKey<LoanApplication>(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        a.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => x.LastPolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        var r = b.Entity<PolicyRevision>();
        r.Property(x => x.DocumentJson).HasColumnType("jsonb"); r.Property(x => x.Kind).HasMaxLength(16);
        r.Property(x => x.DraftVersion).IsConcurrencyToken(); r.Property(x => x.Kind).IsConcurrencyToken();
        r.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => x.BaseRevisionId).OnDelete(DeleteBehavior.Restrict);
        r.HasIndex(x => new { x.PublishedAtUtc, x.Id });
        r.ToTable("PolicyRevisions", t =>
        {
            t.HasCheckConstraint("CK_Policy_Kind", "\"Kind\" IN ('Draft','Published')");
            t.HasCheckConstraint("CK_Policy_Version", "\"DraftVersion\" >= 1 AND \"SchemaVersion\" = 1");
            t.HasCheckConstraint("CK_Policy_Publication", "(\"Kind\" = 'Published') = (\"PublishedAtUtc\" IS NOT NULL)");
        });
        var h = b.Entity<PolicyHead>();
        h.Property(x => x.Id).ValueGeneratedNever(); h.Property(x => x.Version).IsConcurrencyToken();
        h.ToTable("PolicyHeads", t => t.HasCheckConstraint("CK_PolicyHead_Singleton", "\"Id\" = 1 AND \"Version\" >= 1"));
        h.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => x.ActiveRevisionId).OnDelete(DeleteBehavior.Restrict);
        h.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => x.DraftRevisionId).OnDelete(DeleteBehavior.Restrict);
        h.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => x.BaselineRevisionId).OnDelete(DeleteBehavior.Restrict);
        h.HasData(new PolicyHead());
        var o = b.Entity<OutboxMessage>();
        o.Property(x => x.PayloadJson).HasColumnType("jsonb");
        o.Property(x => x.Status).HasMaxLength(16); o.Property(x => x.Operation).HasMaxLength(16);
        o.Property(x => x.LastErrorCode).HasMaxLength(80);
        o.HasIndex(x => new { x.ApplicationId, x.ApplicationVersion }).IsUnique();
        o.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        o.HasIndex(x => new { x.ApplicationId, x.ApplicationVersion, x.Status });
        o.HasOne<LoanApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        o.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => x.PolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        o.ToTable("OutboxMessages", t =>
        {
            t.HasCheckConstraint("CK_Outbox_Status", "\"Status\" IN ('Pending','Failed','Delivered')");
            t.HasCheckConstraint("CK_Outbox_Operation", "\"Operation\" IN ('Created','Updated')");
            t.HasCheckConstraint("CK_Outbox_Versions", "\"SchemaVersion\" = 1 AND \"ApplicationVersion\" >= 1 AND \"AttemptCount\" >= 0");
            t.HasCheckConstraint("CK_Outbox_Lease", "(\"LeaseToken\" IS NULL) = (\"LeaseExpiresAtUtc\" IS NULL)");
        });
        var audit = b.Entity<AuditEvent>();
        audit.Property(x => x.OccurredAtUtc).HasDefaultValueSql("clock_timestamp()");
        audit.Property(x => x.MetadataJson).HasColumnType("jsonb");
        audit.Property(x => x.Component).HasMaxLength(20); audit.Property(x => x.Action).HasMaxLength(80);
        audit.Property(x => x.ActorType).HasMaxLength(20); audit.Property(x => x.ActorRef).HasMaxLength(100);
        audit.Property(x => x.EntityType).HasMaxLength(40); audit.Property(x => x.Outcome).HasMaxLength(20);
        audit.Property(x => x.ReasonCode).HasMaxLength(80);
        audit.HasIndex(x => new { x.OccurredAtUtc, x.Id }).IsDescending();
        audit.HasIndex(x => x.CorrelationId); audit.HasIndex(x => x.OutboxEventId);
        audit.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAtUtc, x.Id }).IsDescending(false, false, true, true);
        audit.ToTable("AuditEvents", t => t.HasCheckConstraint("CK_Audit_Outcome", "\"Outcome\" IN ('Succeeded','Denied')"));
    }
}
