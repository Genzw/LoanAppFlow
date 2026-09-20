using LoanApp.Mock.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LoanApp.Mock.Tests;

public sealed class InboxSchemaTests
{
    private static MockDbContext Open() => new(new DbContextOptionsBuilder<MockDbContext>().UseNpgsql(
        Environment.GetEnvironmentVariable("TEST_MOCK_DB") ?? throw new InvalidOperationException("TEST_MOCK_DB required; use scripts/Test-Local.ps1")).Options);
    [Fact]
    public async Task Inbox_rejects_two_events_for_same_application_version()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync();
        var applicationId = Guid.NewGuid();
        db.Applications.Add(new() { ApplicationId = applicationId, CustomerId = Guid.NewGuid(), Version = 1, SnapshotJson = "{}", UpdatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.InboxReceipts.AddRange(Enumerable.Range(0, 2).Select(_ => new InboxReceipt
        {
            EventId = Guid.NewGuid(), ApplicationId = applicationId, ApplicationVersion = 1, Operation = "Created",
            PayloadHash = new string('a', 64), ReceivedAtUtc = DateTimeOffset.UtcNow
        }));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        await tx.RollbackAsync();
    }
    [Fact]
    public async Task Mock_runtime_cannot_connect_to_main_database_or_erase_audit()
    {
        await using var db = Open();
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_database_privilege(current_user, 'loanapp', 'CONNECT') AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user, '\"AuditEvents\"', 'DELETE') AS \"Value\"").SingleAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
