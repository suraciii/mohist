using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Storage;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;

namespace Mohist.Server.Infrastructure.Persistence.Epic;

public class EpicCounterStore : IStateStore<EpicCounterState>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public EpicCounterStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<EpicCounterState?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.EpicCounters.FindAsync(key);
        return row is null ? null : new EpicCounterState(row.Next);
    }

    public async Task SaveAsync(string key, EpicCounterState state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.EpicCounters.FindAsync(key);
        if (row is null)
        {
            db.EpicCounters.Add(new EpicCounterRow { ProjectId = key, Next = state.Next });
        }
        else
        {
            row.Next = state.Next;
        }
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotSupportedException();

    public Task<IReadOnlyList<EpicCounterState>> ListAsync() => throw new NotSupportedException();
}
