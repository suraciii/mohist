using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Storage.Db.Entities;

namespace Mohist.Server.Issue.Storage;

public sealed class IssueStateStore : IStateStore<Domain.Issue>
{
    private static readonly string IssueType = typeof(Domain.Issue).FullName!;
    private readonly IDbContextFactory<MohistDbContext> _contextFactory;

    public IssueStateStore(IDbContextFactory<MohistDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<Domain.Issue?> LoadAsync(string key)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var row = await db.GrainStates.FindAsync(key, IssueType);
        return row is null ? null : Deserialize(row.JsonState);
    }

    public async Task SaveAsync(string key, Domain.Issue state)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var row = await db.GrainStates.FindAsync(key, IssueType);
        var json = Serialize(state);
        if (row is null)
        {
            db.GrainStates.Add(new GrainState
            {
                Key = key,
                Type = IssueType,
                JsonState = json,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.JsonState = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var row = await db.GrainStates.FindAsync(key, IssueType);
        if (row is not null)
        {
            db.GrainStates.Remove(row);
            await db.SaveChangesAsync();
        }
    }

    public static Domain.Issue? Deserialize(string json) =>
        JsonSerializer.Deserialize<IssueSnapshot>(json)?.ToDomain();

    public static string Serialize(Domain.Issue issue) =>
        JsonSerializer.Serialize(IssueSnapshot.FromDomain(issue));
}
