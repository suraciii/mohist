using Microsoft.EntityFrameworkCore;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Data.Auth;

public sealed class PrincipalStore : IPrincipalStore, IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _time;

    public PrincipalStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task EnsureAgentPrincipalAsync(string principalId, string name, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.Principals.AnyAsync(row => row.Id == principalId, ct).ConfigureAwait(false))
            return;

        db.Principals.Add(new PrincipalRow
        {
            Id = principalId,
            Kind = PrincipalKind.Agent.ToString(),
            Name = name,
            CreatedAt = _time.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent ensure of the same principal won the race; the
            // unique id index is the backstop that makes both callers
            // succeed idempotently.
        }
    }
}
