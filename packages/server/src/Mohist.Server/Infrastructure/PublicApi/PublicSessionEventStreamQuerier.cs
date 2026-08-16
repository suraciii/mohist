using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.PublicApi;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.PublicApi;

/// <summary>
/// Reads one Session's public event stream from the persisted projection.
/// Cursor verification, generation checks, expiry, and the Session
/// freshness gate all complete before the public event rows are queried.
/// </summary>
public sealed class PublicSessionEventStreamQuerier : IScopedService
{
    public const int DefaultLimit = 100;
    public const int MaximumLimit = 100;

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly PublicExecutionReadQuerier _publicReads;
    private readonly PublicSessionEventCursorCodec _cursorCodec;

    public PublicSessionEventStreamQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        PublicExecutionReadQuerier publicReads,
        PublicSessionEventCursorCodec cursorCodec)
    {
        _dbFactory = dbFactory;
        _publicReads = publicReads;
        _cursorCodec = cursorCodec;
    }

    public async Task<PublicSessionEventReadOutcome> ReadAsync(
        string projectId,
        string sessionId,
        string? after,
        int limit,
        CancellationToken ct = default)
    {
        var signer = await _cursorCodec.OpenAsync(ct);
        PublicSessionEventCursorPayload? cursor = null;
        if (after is not null
            && !signer.TryDecode(after, projectId, sessionId, out cursor))
        {
            return PublicSessionEventReadOutcome.InvalidCursor;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stream = await db.PublicStreamStates.AsNoTracking()
            .FirstOrDefaultAsync(row => row.SessionId == sessionId, ct);

        // A valid token cannot be recognized after its stream state was
        // physically purged. This is intentionally not a restart at zero.
        if (cursor is not null
            && (stream is null || cursor.Generation != stream.ActiveGeneration))
        {
            return PublicSessionEventReadOutcome.InvalidCursor;
        }

        if (stream?.Closed == true)
        {
            return cursor is null
                ? PublicSessionEventReadOutcome.Missing
                : PublicSessionEventReadOutcome.Expired(
                    earliestSequence: null,
                    latestSequence: stream.LatestSequence);
        }

        var canonicalProjectId = await db.AgentSessions.AsNoTracking()
            .Where(row => row.Id == sessionId)
            .Select(row => row.LabelProjectId)
            .FirstOrDefaultAsync(ct);
        if (!string.Equals(canonicalProjectId, projectId, StringComparison.Ordinal))
        {
            return PublicSessionEventReadOutcome.Missing;
        }

        // The stream state can be absent while the canonical Session is
        // already durable. In that case the same source watermark gate
        // returns projection_lag instead of a false 404.
        if (await _publicReads.IsSessionProjectionBehindAsync(sessionId, ct))
        {
            return PublicSessionEventReadOutcome.Lag;
        }

        if (stream is null)
        {
            return PublicSessionEventReadOutcome.Missing;
        }

        var highWaterSequence = stream.LatestSequence ?? 0;
        var afterPosition = cursor?.AfterPosition ?? 0;
        if (cursor is not null
            && stream.EarliestSequence is { } earliest
            && afterPosition < earliest)
        {
            return PublicSessionEventReadOutcome.Expired(earliest, stream.LatestSequence);
        }

        var pageLimit = Math.Clamp(limit, 1, MaximumLimit);
        var rows = await db.PublicSessionEvents.AsNoTracking()
            .Where(row => row.SessionId == sessionId
                && row.Generation == stream.ActiveGeneration
                && row.Sequence > afterPosition)
            .OrderBy(row => row.Sequence)
            .Take(pageLimit)
            .ToListAsync(ct);

        var events = new List<PublicSessionEvent>(rows.Count);
        foreach (var row in rows)
        {
            var eventCursor = signer.Encode(new PublicSessionEventCursorPayload(
                projectId,
                sessionId,
                row.Generation,
                row.Sequence,
                PublicSessionEventCursorCodec.CurrentVersion));
            var payload = ParsePayload(row.PayloadJson);
            if (row.Type == PublicSessionEventTypes.ContextReset)
            {
                events.Add(new PublicSessionEvent
                {
                    Sequence = row.Sequence,
                    Cursor = eventCursor,
                    Type = row.Type,
                    OccurredAt = row.OccurredAt,
                    Session = payload,
                });
            }
            else
            {
                events.Add(new PublicSessionEvent
                {
                    Sequence = row.Sequence,
                    Cursor = eventCursor,
                    Type = row.Type,
                    OccurredAt = row.OccurredAt,
                    Execution = payload,
                });
            }
        }

        var nextPosition = events.Count == 0
            ? highWaterSequence
            : events[^1].Sequence;
        var nextCursor = signer.Encode(new PublicSessionEventCursorPayload(
            projectId,
            sessionId,
            stream.ActiveGeneration,
            nextPosition,
            PublicSessionEventCursorCodec.CurrentVersion));

        return PublicSessionEventReadOutcome.Found(new PublicEventPage
        {
            SessionId = sessionId,
            Events = events,
            NextCursor = nextCursor,
            HighWaterSequence = highWaterSequence,
        });
    }

    private static JsonElement ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("A public Session event payload must be a JSON object.");
        }

        return document.RootElement.Clone();
    }
}

public enum PublicSessionEventReadStatus
{
    Found,
    NotFound,
    ProjectionLag,
    CursorInvalid,
    CursorExpired,
}

public sealed record PublicSessionEventReadOutcome(
    PublicSessionEventReadStatus Status,
    PublicEventPage? Page,
    long? EarliestSequence,
    long? LatestSequence)
{
    public static PublicSessionEventReadOutcome Found(PublicEventPage page) =>
        new(PublicSessionEventReadStatus.Found, page, null, null);

    public static PublicSessionEventReadOutcome Missing { get; } =
        new(PublicSessionEventReadStatus.NotFound, null, null, null);

    public static PublicSessionEventReadOutcome Lag { get; } =
        new(PublicSessionEventReadStatus.ProjectionLag, null, null, null);

    public static PublicSessionEventReadOutcome InvalidCursor { get; } =
        new(PublicSessionEventReadStatus.CursorInvalid, null, null, null);

    public static PublicSessionEventReadOutcome Expired(
        long? earliestSequence,
        long? latestSequence) =>
        new(PublicSessionEventReadStatus.CursorExpired, null, earliestSequence, latestSequence);
}
