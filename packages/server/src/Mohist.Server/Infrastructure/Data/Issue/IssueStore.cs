using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Infrastructure.Data.Issue;

public class IssueStore : IStateStore<DomainIssue>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IssueStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<DomainIssue?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.Issues.FindAsync(key);
        return row is null ? null : Deserialize(row.State);
    }

    public async Task SaveAsync(string key, DomainIssue state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.Issues.FindAsync(state.Id);
        var json = Serialize(state);
        if (row is null)
        {
            db.Issues.Add(new IssueRow { IssueId = state.Id, State = json });
        }
        else
        {
            row.State = json;
        }
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotImplementedException();

    public Task<IReadOnlyList<DomainIssue>> ListAsync() => throw new NotImplementedException();

    public static DomainIssue? Deserialize(string json) =>
        JsonSerializer.Deserialize<DomainIssue>(json, JsonOptions);

    public static string Serialize(DomainIssue issue) =>
        JsonSerializer.Serialize(issue, JsonOptions);
}
