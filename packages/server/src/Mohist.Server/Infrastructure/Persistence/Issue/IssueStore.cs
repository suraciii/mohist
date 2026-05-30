using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;

namespace Mohist.Server.Infrastructure.Persistence.Issue;

public class IssueStore : IStateStore<DomainIssue>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<DomainIssue?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueStates.FindAsync(key);
        return row is null ? null : Deserialize(row.StateJson);
    }

    public async Task SaveAsync(string key, DomainIssue state)
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

    public Task<IReadOnlyList<DomainIssue>> ListAsync() => throw new NotSupportedException();

    public static DomainIssue? Deserialize(string json)
    {
        var snapshot = JsonSerializer.Deserialize<IssueSnapshot>(json);
        return snapshot?.ToDomain();
    }

    public static string Serialize(DomainIssue issue) =>
        JsonSerializer.Serialize(IssueSnapshot.FromDomain(issue));
}
