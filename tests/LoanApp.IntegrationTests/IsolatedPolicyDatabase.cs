using LoanApp.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace LoanApp.IntegrationTests;

// A private schema per test allows real commits on independent connections without
// weakening the runtime role or sharing mutable state with other tests/the demo.
public abstract class IsolatedPolicyDatabase : IAsyncLifetime
{
    private readonly string schema = "policy_test_" + Guid.NewGuid().ToString("N");
    private string migrationConnection = "";
    private string runtimeConnection = "";
    public async Task InitializeAsync()
    {
        var migration = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_APP_MIGRATION_DB")
            ?? throw new InvalidOperationException("Run scripts/Test-Local.ps1"));
        if (migration.Database is null || !System.Text.RegularExpressions.Regex.IsMatch(migration.Database, "^loanapp_test_[a-f0-9]{32}$"))
            throw new InvalidOperationException("Isolated tests require a generated test database");
        migration.Pooling = false; migration.SearchPath = schema; migrationConnection = migration.ConnectionString;
        var runtime = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_APP_DB")) { SearchPath = schema, Pooling = false };
        runtimeConnection = runtime.ConnectionString;
        await using var connection = new NpgsqlConnection(migrationConnection); await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection)) await create.ExecuteNonQueryAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(migrationConnection).Options);
        await db.Database.MigrateAsync();
        await using var grant = new NpgsqlCommand($"""
            GRANT USAGE ON SCHEMA {schema} TO loanapp_runtime;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO loanapp_runtime;
            REVOKE UPDATE, DELETE, TRUNCATE ON {schema}."AuditEvents" FROM loanapp_runtime;
            """, connection);
        await grant.ExecuteNonQueryAsync();
    }
    protected DbContextOptions<AppDbContext> Options(params IInterceptor[] interceptors) => new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(runtimeConnection).AddInterceptors(interceptors).Options;
    protected AppDbContext Open(params IInterceptor[] interceptors) => new(Options(interceptors));
    public async Task DisposeAsync()
    {
        if (migrationConnection.Length == 0) return;
        await using var connection = new NpgsqlConnection(migrationConnection); await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
