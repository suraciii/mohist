using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Storage.Db.Entities;

namespace Mohist.Server.Issue.Storage;

public sealed class IssueAggregate
{
    public Domain.Issue Issue { get; }
    public WorkflowProfiles.IssueWorkflowProfile? Profile { get; }

    public IssueAggregate(Domain.Issue issue, WorkflowProfiles.IssueWorkflowProfile? profile)
    {
        Issue = issue;
        Profile = profile;
    }
}

public sealed class IssueStateStore : IStateStore<IssueAggregate>
{
    private static readonly string IssueType = typeof(IssueAggregate).FullName!;
    private readonly IDbContextFactory<MohistDbContext> _contextFactory;

    public IssueStateStore(IDbContextFactory<MohistDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IssueAggregate?> LoadAsync(string key)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var row = await db.GrainStates.FindAsync(key, IssueType);
        return row is null ? null : Deserialize(row.JsonState);
    }

    public async Task SaveAsync(string key, IssueAggregate state)
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

    public Task<IReadOnlyList<IssueAggregate>> ListAsync()
    {
        throw new NotSupportedException();
    }

    public static IssueAggregate? Deserialize(string json)
    {
        var snapshot = JsonSerializer.Deserialize<IssueSnapshot>(json);
        if (snapshot is null) return null;
        var (issue, profile) = snapshot.ToDomain();
        return new IssueAggregate(issue, profile);
    }

    public static string Serialize(IssueAggregate aggregate) =>
        JsonSerializer.Serialize(IssueSnapshot.FromDomain(aggregate.Issue, aggregate.Profile));
}
