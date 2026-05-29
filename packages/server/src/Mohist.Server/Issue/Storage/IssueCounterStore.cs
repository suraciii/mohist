using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Issue.Storage;

public class IssueCounterStore : IStateStore<IssueCounterState>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueCounterStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IssueCounterState?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueCounters.FindAsync(key);
        return row is null ? null : new IssueCounterState(row.Next);
    }

    public async Task SaveAsync(string key, IssueCounterState state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueCounters.FindAsync(key);
        if (row is null)
        {
            db.IssueCounters.Add(new IssueCounterRow { ProjectId = key, Next = state.Next });
        }
        else
        {
            row.Next = state.Next;
        }
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotSupportedException();

    public Task<IReadOnlyList<IssueCounterState>> ListAsync() => throw new NotSupportedException();
}

public class IssueCounterRow
{
    public string ProjectId { get; set; } = string.Empty;
    public int Next { get; set; } = 1;
}