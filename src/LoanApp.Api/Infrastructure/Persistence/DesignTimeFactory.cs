using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LoanApp.Api.Infrastructure.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__AppDb") ??
            "Host=127.0.0.1;Database=loanapp;Username=loanapp_migrator")
        .Options);
}
