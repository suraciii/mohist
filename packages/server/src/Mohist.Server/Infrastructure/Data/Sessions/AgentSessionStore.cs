using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Sessions;

public interface IAgentSessionStore : IStateStore<AgentSession>
{
    Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default);
    Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, IReadOnlyList<AgentSessionRuntimeEventRow> runtimeEvents, CancellationToken ct = default);
    Task SaveTranscriptAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, IReadOnlyList<AgentSessionTranscriptSegmentRow> transcriptSegments, CancellationToken ct = default);
}

public class AgentSessionStore : IAgentSessionStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public AgentSessionStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<AgentSession?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.AgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == key);
        return row is null ? null : AgentSessionJson.Deserialize(row);
    }

    public async Task<IReadOnlyList<AgentSession>> ListAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.AgentSessions.AsNoTracking().ToListAsync();
        return rows.Select(AgentSessionJson.Deserialize).OfType<AgentSession>().ToList();
    }

    public async Task SaveAsync(string key, AgentSession state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await StageSessionAsync(db, key, state);
        await db.SaveChangesAsync();
    }

    public async Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageSessionAsync(db, key, state, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, IReadOnlyList<AgentSessionRuntimeEventRow> runtimeEvents, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.AgentSessionRuntimeEvents.AddRange(runtimeEvents);
            await StageSessionAsync(db, key, state, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SaveTranscriptAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, IReadOnlyList<AgentSessionTranscriptSegmentRow> transcriptSegments, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.AgentSessionTranscriptSegments.AddRange(transcriptSegments);
            await StageSessionAsync(db, key, state, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task StageSessionAsync(MohistDbContext db, string key, AgentSession state, CancellationToken ct = default)
    {
        var row = AgentSessionJson.ToRow(state, DateTime.UtcNow);
        row.Id = key;
        var existing = await db.AgentSessions.FindAsync([key], ct);
        if (existing is null)
        {
            db.AgentSessions.Add(row);
        }
        else
        {
            row.WorkId ??= existing.WorkId;
            row.WorkType ??= existing.WorkType;
            row.Stage ??= existing.Stage;
            db.Entry(existing).CurrentValues.SetValues(row);
        }
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.AgentSessions.FindAsync(key);
        if (session is not null)
        {
            db.AgentSessions.Remove(session);
            await db.SaveChangesAsync();
        }
    }

}

public static class AgentSessionJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AgentSession? Deserialize(AgentSessionRow row)
    {
        try
        {
            var session = JsonSerializer.Deserialize<AgentSession>(row.State, JsonOptions);
            return session is null ? null : Normalize(session, row);
        }
        catch
        {
            return null;
        }
    }

    public static AgentSessionRow ToRow(AgentSession session, DateTime updatedAt) => new()
    {
        Id = session.Id,
        State = JsonSerializer.Serialize(session, JsonOptions),
        ProjectId = session.ProjectId,
        IssueNumber = session.IssueNumber,
        WorkflowRunId = session.RunId,
        SessionName = session.SessionName,
        WorkId = session.TaskId,
        WorkType = session.TaskKind,
        Stage = session.Phase,
        RunnerId = session.Runtime.RunnerId,
        AgentSessionId = session.Status.AgentRuntimeSessionId,
        Status = AgentSessionStatusNames.ToName(session.Status.Phase),
        CreatedAt = session.Status.CreatedAt,
        LastDataAt = session.Status.LastDataAt,
        CompletedAt = session.Status.CompletedAt,
        UpdatedAt = updatedAt,
    };

    private static AgentSession Normalize(AgentSession session, AgentSessionRow row)
    {
        session.Metadata = session.Metadata
            .WithLabel(AgentSessionMetadataKeys.ProjectId, string.IsNullOrWhiteSpace(session.ProjectId) ? row.ProjectId : null)
            .WithLabel(AgentSessionMetadataKeys.IssueNumber, session.IssueNumber == 0 && row.IssueNumber > 0 ? row.IssueNumber.ToString() : null)
            .WithLabel(AgentSessionMetadataKeys.SourceKind, string.IsNullOrWhiteSpace(session.SourceKind) ? "workflow" : null)
            .WithLabel(AgentSessionMetadataKeys.SourceId, string.IsNullOrWhiteSpace(session.RunId) ? row.WorkflowRunId : null)
            .WithLabel(AgentSessionMetadataKeys.SessionName, string.IsNullOrWhiteSpace(session.SessionName) ? row.SessionName : null);

        if (string.IsNullOrWhiteSpace(session.Id)
            || string.IsNullOrWhiteSpace(session.ProjectId)
            || string.IsNullOrWhiteSpace(session.RunId)
            || string.IsNullOrWhiteSpace(session.SessionName))
            throw new InvalidOperationException("AgentSession state is missing required fields.");

        if (session.IssueNumber == 0)
            throw new InvalidOperationException("AgentSession state is missing IssueNumber.");
        if (session.Runtime is null)
            session.Runtime = new AgentSessionRuntime(row.RunnerId ?? string.Empty, "opencode", null);
        else if (string.IsNullOrWhiteSpace(session.Runtime.AgentRuntime))
            session.Runtime = session.Runtime with { AgentRuntime = "opencode" };
        if (session.Status.CreatedAt == default)
            throw new InvalidOperationException("AgentSession state is missing CreatedAt.");
        var phase = session.Status.Phase;
        if (phase == AgentSessionStatus.Created && !string.Equals(row.Status, AgentSessionStatusNames.ToName(AgentSessionStatus.Created), StringComparison.OrdinalIgnoreCase))
            phase = AgentSessionStatusNames.Parse(row.Status);
        session.Status = session.Status with
        {
            Phase = phase,
            AgentRuntimeSessionId = session.Status.AgentRuntimeSessionId ?? row.AgentSessionId,
            LastDataAt = session.Status.LastDataAt ?? row.LastDataAt,
            CompletedAt = session.Status.CompletedAt ?? row.CompletedAt,
            UsageSummary = session.Status.UsageSummary ?? new AgentUsageSummary()
        };
        return session;
    }
}
