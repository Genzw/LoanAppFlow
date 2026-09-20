using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LoanApp.Mock.Persistence;

public sealed class ExternalApplication
{
    public Guid ApplicationId { get; set; }
    public Guid CustomerId { get; set; }
    public long Version { get; set; }
    public required string SnapshotJson { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
public sealed class InboxReceipt
{
    public Guid EventId { get; set; }
    public Guid ApplicationId { get; set; }
    public long ApplicationVersion { get; set; }
    public required string Operation { get; set; }
    public required string PayloadHash { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
public sealed class MockAuditEvent
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string Component { get; set; }
    public required string Action { get; set; }
    public Guid CorrelationId { get; set; }
    public required string ActorType { get; set; }
    public string? ActorRef { get; set; }
    public required string EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public required string Outcome { get; set; }
    public string? ReasonCode { get; set; }
    public Guid? PolicyRevisionId { get; set; }
    public Guid? OutboxEventId { get; set; }
    public string MetadataJson { get; set; } = "{}";
}
public sealed class MockDbContext(DbContextOptions<MockDbContext> options) : DbContext(options)
{
    public DbSet<ExternalApplication> Applications => Set<ExternalApplication>();
    public DbSet<InboxReceipt> InboxReceipts => Set<InboxReceipt>();
    public DbSet<MockAuditEvent> AuditEvents => Set<MockAuditEvent>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        var a = b.Entity<ExternalApplication>(); a.ToTable("ExternalApplications"); a.HasKey(x => x.ApplicationId);
        a.Property(x => x.Version).IsConcurrencyToken(); a.Property(x => x.SnapshotJson).HasColumnType("jsonb");
        var i = b.Entity<InboxReceipt>(); i.HasKey(x => x.EventId);
        i.HasIndex(x => new { x.ApplicationId, x.ApplicationVersion }).IsUnique();
        i.Property(x => x.Operation).HasMaxLength(16); i.Property(x => x.PayloadHash).HasMaxLength(64);
        i.HasOne<ExternalApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        var audit = b.Entity<MockAuditEvent>(); audit.ToTable("AuditEvents");
        audit.Property(x => x.OccurredAtUtc).HasDefaultValueSql("clock_timestamp()");
        audit.Property(x => x.MetadataJson).HasColumnType("jsonb");
        audit.Property(x => x.Component).HasMaxLength(20); audit.Property(x => x.Action).HasMaxLength(80);
        audit.Property(x => x.ActorType).HasMaxLength(20); audit.Property(x => x.ActorRef).HasMaxLength(100);
        audit.Property(x => x.EntityType).HasMaxLength(40); audit.Property(x => x.Outcome).HasMaxLength(20);
        audit.Property(x => x.ReasonCode).HasMaxLength(80);
        audit.HasIndex(x => new { x.OccurredAtUtc, x.Id }).IsDescending();
        audit.HasIndex(x => x.CorrelationId); audit.HasIndex(x => x.OutboxEventId);
        audit.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAtUtc, x.Id }).IsDescending(false, false, true, true);
    }
}
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MockDbContext>
{
    public MockDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<MockDbContext>().UseNpgsql(
        Environment.GetEnvironmentVariable("ConnectionStrings__MockDb") ?? "Host=127.0.0.1;Database=loanapp_mock;Username=loanapp_mock_migrator").Options);
}
