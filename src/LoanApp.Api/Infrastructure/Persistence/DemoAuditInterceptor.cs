using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace LoanApp.Api.Infrastructure.Persistence;
public sealed class DemoAuditInterceptor(IHttpContextAccessor http) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (http.HttpContext?.Items["DemoSessionId"] is Guid actor && data.Context is { } db)
            foreach (var entry in db.ChangeTracker.Entries<AuditEvent>().Where(e => e.State == EntityState.Added && e.Entity.ActorType == "Local"))
            { entry.Entity.ActorType = "DemoSession"; entry.Entity.ActorRef = actor.ToString(); }
        return base.SavingChangesAsync(data, result, ct);
    }
}
