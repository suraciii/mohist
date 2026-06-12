using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Sessions;

public interface IAgentSessionStore : IStateStore<AgentSession>
{
    Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default);
    Task SaveTranscriptAsync(string key, AgentSession state, AgentSessionTranscriptFlush transcript, CancellationToken ct = default);
}

public sealed record AgentSessionTranscriptFlush(
    bool StartNewTurn,
    AgentSessionTranscriptTurnUpsert Turn,
    IReadOnlyList<AgentSessionTranscriptPartDelta> Parts);

public sealed record AgentSessionTranscriptTurnUpsert(
    string SessionId,
    string ProjectId,
    int IssueNumber,
    string WorkflowRunId,
    string SessionName,
    string? AgentSessionId,
    long Sequence,
    string PromptText,
    string PromptKind,
    DateTime StartedAt,
    DateTime UpdatedAt);

public sealed record AgentSessionTranscriptPartDelta(
    string SessionId,
    string ProjectId,
    int IssueNumber,
    string WorkflowRunId,
    string SessionName,
    string? AgentSessionId,
    string? WorkId,
    string? WorkType,
    string? Stage,
    string Type,
    string CorrelationKey,
    string? CorrelationId,
    string? TextDelta,
    string PayloadJson,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    int RawEventCount);

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

    public async Task SaveTranscriptAsync(string key, AgentSession state, AgentSessionTranscriptFlush transcript, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageSessionAsync(db, key, state, ct);

            var turn = transcript.StartNewTurn
                ? null
                : await db.AgentSessionTranscriptTurns
                    .OrderByDescending(t => t.Sequence)
                    .FirstOrDefaultAsync(t => t.SessionId == transcript.Turn.SessionId, ct);
            if (turn is null)
            {
                var sequence = transcript.StartNewTurn || transcript.Turn.Sequence <= 0
                    ? (await db.AgentSessionTranscriptTurns
                        .Where(t => t.SessionId == transcript.Turn.SessionId)
                        .Select(t => (long?)t.Sequence)
                        .MaxAsync(ct) ?? 0) + 1
                    : transcript.Turn.Sequence;
                turn = new AgentSessionTranscriptTurnRow
                {
                    SessionId = transcript.Turn.SessionId,
                    ProjectId = transcript.Turn.ProjectId,
                    IssueNumber = transcript.Turn.IssueNumber,
                    WorkflowRunId = transcript.Turn.WorkflowRunId,
                    SessionName = transcript.Turn.SessionName,
                    AgentSessionId = transcript.Turn.AgentSessionId,
                    Sequence = sequence,
                    PromptText = transcript.Turn.PromptText,
                    PromptKind = transcript.Turn.PromptKind,
                    StartedAt = transcript.Turn.StartedAt,
                    UpdatedAt = transcript.Turn.UpdatedAt,
                };
                db.AgentSessionTranscriptTurns.Add(turn);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                turn.ProjectId = transcript.Turn.ProjectId;
                turn.IssueNumber = transcript.Turn.IssueNumber;
                turn.WorkflowRunId = transcript.Turn.WorkflowRunId;
                turn.SessionName = transcript.Turn.SessionName;
                turn.AgentSessionId = transcript.Turn.AgentSessionId ?? turn.AgentSessionId;
                if (!string.IsNullOrWhiteSpace(transcript.Turn.PromptText))
                    turn.PromptText = transcript.Turn.PromptText;
                turn.PromptKind = transcript.Turn.PromptKind;
                turn.UpdatedAt = transcript.Turn.UpdatedAt;
            }

            if (transcript.Parts.Count > 0)
            {
                var types = transcript.Parts.Select(p => p.Type).Distinct(StringComparer.Ordinal).ToArray();
                var keys = transcript.Parts.Select(p => p.CorrelationKey).Distinct(StringComparer.Ordinal).ToArray();
                var existingParts = await db.AgentSessionTranscriptParts
                    .Where(p => p.TurnId == turn.Id && types.Contains(p.Type) && keys.Contains(p.CorrelationKey))
                    .ToListAsync(ct);
                var existingByKey = existingParts.ToDictionary(p => PartKey(p.Type, p.CorrelationKey), StringComparer.Ordinal);
                var nextSequence = (await db.AgentSessionTranscriptParts
                    .Where(p => p.TurnId == turn.Id)
                    .Select(p => (long?)p.Sequence)
                    .MaxAsync(ct) ?? 0) + 1;

                foreach (var delta in transcript.Parts)
                {
                    var partKey = PartKey(delta.Type, delta.CorrelationKey);
                    if (existingByKey.TryGetValue(partKey, out var part))
                    {
                        part.AgentSessionId = delta.AgentSessionId ?? part.AgentSessionId;
                        part.WorkId = delta.WorkId ?? part.WorkId;
                        part.WorkType = delta.WorkType ?? part.WorkType;
                        part.Stage = delta.Stage ?? part.Stage;
                        part.CorrelationId = delta.CorrelationId ?? part.CorrelationId;
                        if (!string.IsNullOrEmpty(delta.TextDelta))
                            part.Text += delta.TextDelta;
                        part.PayloadJson = string.IsNullOrWhiteSpace(delta.PayloadJson) ? part.PayloadJson : delta.PayloadJson;
                        part.LastSeenAt = delta.LastSeenAt;
                        part.RawEventCount += Math.Max(0, delta.RawEventCount);
                        continue;
                    }

                    part = new AgentSessionTranscriptPartRow
                    {
                        TurnId = turn.Id,
                        SessionId = delta.SessionId,
                        ProjectId = delta.ProjectId,
                        IssueNumber = delta.IssueNumber,
                        WorkflowRunId = delta.WorkflowRunId,
                        SessionName = delta.SessionName,
                        AgentSessionId = delta.AgentSessionId,
                        WorkId = delta.WorkId,
                        WorkType = delta.WorkType,
                        Stage = delta.Stage,
                        Sequence = nextSequence++,
                        Type = delta.Type,
                        CorrelationKey = delta.CorrelationKey,
                        CorrelationId = delta.CorrelationId,
                        Text = delta.TextDelta ?? string.Empty,
                        PayloadJson = string.IsNullOrWhiteSpace(delta.PayloadJson) ? "{}" : delta.PayloadJson,
                        FirstSeenAt = delta.FirstSeenAt,
                        LastSeenAt = delta.LastSeenAt,
                        RawEventCount = Math.Max(0, delta.RawEventCount),
                    };
                    db.AgentSessionTranscriptParts.Add(part);
                    existingByKey[partKey] = part;
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static string PartKey(string type, string correlationKey) => $"{type}\u001f{correlationKey}";

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
        CompletedAt = null,
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
        var phase = !string.IsNullOrWhiteSpace(session.Status.AgentRuntimeSessionId) || !string.IsNullOrWhiteSpace(row.AgentSessionId)
            ? AgentSessionStatus.Bound
            : AgentSessionStatus.Opened;
        session.Status = session.Status with
        {
            Phase = phase,
            AgentRuntimeSessionId = session.Status.AgentRuntimeSessionId ?? row.AgentSessionId,
            LastDataAt = session.Status.LastDataAt ?? row.LastDataAt,
            UsageSummary = session.Status.UsageSummary ?? new AgentUsageSummary()
        };
        return session;
    }
}
