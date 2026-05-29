using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Issue.Storage;

public class IssueProfileStore : IStateStore<IssueWorkflowProfile>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueProfileStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IssueWorkflowProfile?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueProfiles.FindAsync(key);
        return row is null ? null : Deserialize(row.StateJson);
    }

    public async Task SaveAsync(string key, IssueWorkflowProfile state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueProfiles.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
        {
            db.IssueProfiles.Add(new IssueProfileRow { Key = key, StateJson = json });
        }
        else
        {
            row.StateJson = json;
        }
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotSupportedException();

    public Task<IReadOnlyList<IssueWorkflowProfile>> ListAsync() => throw new NotSupportedException();

    public static IssueWorkflowProfile? Deserialize(string json) =>
        IssueWorkflowProfileSnapshot.Deserialize(json);

    public static string Serialize(IssueWorkflowProfile profile) =>
        JsonSerializer.Serialize(IssueWorkflowProfileSnapshot.FromDomain(profile));
}

public class IssueProfileRow
{
    public string Key { get; set; } = string.Empty;
    public string StateJson { get; set; } = "{}";
}
