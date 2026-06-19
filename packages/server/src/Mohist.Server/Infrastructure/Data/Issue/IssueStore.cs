using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Infrastructure.Data.Issue;

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
            db.Issues.Add(new IssueRow { IssueId = state.Id, State = json, Risk = state.Risk });
        }
        else
        {
            row.State = json;
            row.Risk = state.Risk;
        }
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotImplementedException();

    public Task<IReadOnlyList<DomainIssue>> ListAsync() => throw new NotImplementedException();

    public static DomainIssue? Deserialize(string json) =>
        Deserialize(json, out _);

    public static DomainIssue? Deserialize(string json, out bool legacyLabelsDiscarded)
    {
        legacyLabelsDiscarded = false;
        if (string.IsNullOrEmpty(json)) return null;

        var (rewritten, discarded) = NormalizeLegacyLabels(json);
        if (discarded) legacyLabelsDiscarded = true;
        return JSON.Deserialize<DomainIssue>(rewritten);
    }

    private static (string Json, bool Discarded) NormalizeLegacyLabels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("labels", out var labelsElement))
                return (json, false);

            if (labelsElement.ValueKind == JsonValueKind.Object)
                return (json, false);

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("labels")) continue;
                    property.WriteTo(writer);
                }
                writer.WritePropertyName("labels");
                writer.WriteStartObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return (System.Text.Encoding.UTF8.GetString(ms.ToArray()), true);
        }
        catch (JsonException)
        {
            return (json, false);
        }
    }

    public static string Serialize(DomainIssue issue) =>
        JSON.Serialize(issue);
}
