using LoanApp.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LoanApp.IntegrationTests;

public sealed class PersistenceTests
{
    private static AppDbContext Open() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(
        Environment.GetEnvironmentVariable("TEST_APP_DB") ?? throw new InvalidOperationException("TEST_APP_DB required; run scripts/Test-Local.ps1"))
        .Options);
    private static Customer Customer(string ssn) => new()
    {
        Id = Guid.NewGuid(), Ssn = ssn, FirstName = "Test", LastName = "Fixture", AddressLine1 = "Demo",
        City = "Demo", State = "CA", PostalCode = "00000", CompanyName = "Test", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
    };
    private static PolicyRevision Policy() => new()
    {
        Id = Guid.NewGuid(), Kind = "Published", DocumentJson = "{\"schemaVersion\":1,\"rules\":[],\"blacklist\":[]}",
        CreatedAtUtc = DateTimeOffset.UtcNow, PublishedAtUtc = DateTimeOffset.UtcNow
    };
    [Fact]
    public async Task Schema_exists_on_PostgreSQL17_and_head_is_technical_only()
    {
        await using var db = Open();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var version = await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").SingleAsync();
        Assert.Contains("PostgreSQL 17.", version);
        Assert.Equal(1, (await db.PolicyHeads.SingleAsync()).Id);
    }
    [Fact]
    public async Task Unique_ssn_rejects_duplicate_and_rolls_back_entire_batch()
    {
        var ssn = Random.Shared.Next(100000000, 999999999).ToString();
        await using (var db = Open())
        {
            db.Customers.AddRange(Customer(ssn), Customer(ssn));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await using var verify = Open();
        Assert.False(await verify.Customers.AnyAsync(c => c.Ssn == ssn));
    }
    [Fact]
    public async Task Unique_application_per_customer_is_enforced()
    {
        await using var db = Open(); await using var transaction = await db.Database.BeginTransactionAsync();
        var customer = Customer(Random.Shared.Next(100000000, 999999999).ToString()); var policy = Policy();
        db.AddRange(customer, policy); await db.SaveChangesAsync();
        var applications = Enumerable.Range(0, 2).Select(_ => new LoanApplication
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LastPolicyRevisionId = policy.Id,
            RequestedAmount = 123.45m, Version = 1, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        }).ToArray();
        db.Applications.Add(applications[0]); await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.Applications.Add(applications[1]);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        await transaction.RollbackAsync();
    }
    [Fact]
    public async Task Invalid_outbox_causes_customer_application_and_audit_rollback()
    {
        var customer = Customer(Random.Shared.Next(100000000, 999999999).ToString()); var policy = Policy();
        var app = new LoanApplication { Id = Guid.NewGuid(), CustomerId = customer.Id, LastPolicyRevisionId = policy.Id,
            RequestedAmount = 999999999.99m, Version = 1, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await using (var db = Open())
        {
            db.AddRange(customer, policy, app);
            db.OutboxMessages.Add(new() { Id = Guid.NewGuid(), ApplicationId = app.Id, ApplicationVersion = 1,
                PolicyRevisionId = policy.Id, CorrelationId = Guid.NewGuid(), Operation = "INVALID", PayloadJson = "{}",
                NextAttemptAtUtc = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow });
            db.AuditEvents.Add(new() { Id = Guid.NewGuid(), Component = "Api", Action = "Application.Created", CorrelationId = Guid.NewGuid(),
                ActorType = "Local", EntityType = "Application", EntityId = app.Id, Outcome = "Succeeded" });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await using var verify = Open();
        Assert.False(await verify.Customers.AnyAsync(c => c.Id == customer.Id));
        Assert.False(await verify.Applications.AnyAsync(a => a.Id == app.Id));
        Assert.False(await verify.PolicyRevisions.AnyAsync(p => p.Id == policy.Id));
        Assert.False(await verify.AuditEvents.AnyAsync(e => e.EntityId == app.Id));
    }
    [Fact]
    public async Task Runtime_audit_permissions_are_append_only_and_schema_is_not_writable()
    {
        await using var db = Open();
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user, '\"AuditEvents\"', 'INSERT') AS \"Value\"").SingleAsync());
        foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" })
            Assert.False(await db.Database.SqlQuery<bool>($"SELECT has_table_privilege(current_user, '\"AuditEvents\"', {privilege}) AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_schema_privilege(current_user, 'public', 'CREATE') AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_database_privilege(current_user, 'loanapp_mock', 'CONNECT') AS \"Value\"").SingleAsync());
    }
    [Fact]
    public async Task Numeric_precision_and_jsonb_survive_roundtrip()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync();
        var customer = Customer(Random.Shared.Next(100000000, 999999999).ToString()); var policy = Policy();
        var app = new LoanApplication { Id = Guid.NewGuid(), CustomerId = customer.Id, LastPolicyRevisionId = policy.Id,
            RequestedAmount = 999999999.99m, Version = 1, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        db.AddRange(customer, policy, app); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(999999999.99m, (await db.Applications.SingleAsync(a => a.Id == app.Id)).RequestedAmount);
        Assert.Contains("schemaVersion", (await db.PolicyRevisions.SingleAsync(p => p.Id == policy.Id)).DocumentJson);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Stale_application_version_cannot_overwrite_an_update()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync();
        var customer = Customer(Random.Shared.Next(100000000, 999999999).ToString()); var policy = Policy();
        var app = new LoanApplication { Id = Guid.NewGuid(), CustomerId = customer.Id, LastPolicyRevisionId = policy.Id,
            RequestedAmount = 123.45m, Version = 1, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        db.AddRange(customer, policy, app); await db.SaveChangesAsync();
        app.Version = 2; app.RequestedAmount = 200m; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var stale = new LoanApplication { Id = app.Id, CustomerId = customer.Id, LastPolicyRevisionId = policy.Id,
            RequestedAmount = 300m, Version = 1, CreatedAtUtc = app.CreatedAtUtc, UpdatedAtUtc = app.UpdatedAtUtc };
        db.Attach(stale); stale.Version = 2; db.Entry(stale).Property(x => x.RequestedAmount).IsModified = true;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); Assert.Equal(200m, (await db.Applications.SingleAsync(a => a.Id == app.Id)).RequestedAmount);
        await tx.RollbackAsync();
    }
}
