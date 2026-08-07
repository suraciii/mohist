using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public sealed class ProjectRecentEventReader : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public ProjectRecentEventReader(IDbContextFactory<MohistDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<ProjectRecentEvent>> ListAsync(
        string projectId,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return [];
        limit = limit <= 0 ? 20 : limit;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = new List<ProjectRecentEventProjection>();
        rows.AddRange((await db.IssueEvents.AsNoTracking().ToListAsync(ct)).Select(row => ProjectRecentEventProjection.From(row.Id, row.EventId, row.Type, row.Source, row.Subject, row.Time, row.ExtensionsJson)));
        rows.AddRange((await db.WorkflowRunEvents.AsNoTracking().ToListAsync(ct)).Select(row => ProjectRecentEventProjection.From(row.Id, row.EventId, row.Type, row.Source, row.Subject, row.Time, row.ExtensionsJson)));
        rows.AddRange((await db.EpicEvents.AsNoTracking().ToListAsync(ct)).Select(row => ProjectRecentEventProjection.From(row.Id, row.EventId, row.Type, row.Source, row.Subject, row.Time, row.ExtensionsJson)));
        rows.AddRange((await db.AgentSessionEvents.AsNoTracking().ToListAsync(ct)).Select(row => ProjectRecentEventProjection.From(row.Id, row.EventId, row.Type, row.Source, row.Subject, row.Time, row.ExtensionsJson)));
        rows.AddRange((await db.WorkspaceEvents.AsNoTracking().ToListAsync(ct)).Select(row => ProjectRecentEventProjection.From(row.Id, row.EventId, row.Type, row.Source, row.Subject, row.Time, row.ExtensionsJson)));

        return rows
            .Where(row => row.Extensions.TryGetValue("projectid", out var stampedProject)
                && string.Equals(stampedProject, projectId, StringComparison.Ordinal))
            .OrderByDescending(row => row.Time)
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .Select(row => new ProjectRecentEvent(
                row.EventId,
                row.Type,
                row.Source,
                row.Time,
                new PersistedEventMatchInput(row.Type, row.Source, row.Subject, row.Extensions)))
            .ToList();
    }

    private sealed record ProjectRecentEventProjection(
        long Id,
        string EventId,
        string Type,
        string Source,
        string? Subject,
        DateTimeOffset Time,
        IReadOnlyDictionary<string, string> Extensions)
    {
        public static ProjectRecentEventProjection From(
            long id,
            string eventId,
            string type,
            string source,
            string? subject,
            DateTimeOffset time,
            string extensionsJson)
        {
            IReadOnlyDictionary<string, string> extensions;
            try
            {
                extensions = JsonSerializer.Deserialize<Dictionary<string, string>>(extensionsJson)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                extensions = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return new(id, eventId, type, source, subject, time, extensions);
        }
    }
}

public sealed record ProjectRecentEvent(
    string EventId,
    string Type,
    string Source,
    DateTimeOffset Time,
    EventMatchInput Input);
