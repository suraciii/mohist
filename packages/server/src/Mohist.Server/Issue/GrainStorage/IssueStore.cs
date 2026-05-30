using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Issue.GrainStorage;

public class IssueStore : IStateStore<Domain.Issue>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<Domain.Issue?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueStates.FindAsync(key);
        return row is null ? null : Deserialize(row.StateJson);
    }

    public async Task SaveAsync(string key, Domain.Issue state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueStates.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
        {
            db.IssueStates.Add(new IssueStateRow { Key = key, StateJson = json });
        }
        else
        {
            row.StateJson = json;
        }
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotSupportedException();

    public Task<IReadOnlyList<Domain.Issue>> ListAsync() => throw new NotSupportedException();

    public static Domain.Issue? Deserialize(string json)
    {
        var snapshot = JsonSerializer.Deserialize<IssueSnapshot>(json);
        return snapshot?.ToDomain();
    }

    public static string Serialize(Domain.Issue issue) =>
        JsonSerializer.Serialize(IssueSnapshot.FromDomain(issue));
}
