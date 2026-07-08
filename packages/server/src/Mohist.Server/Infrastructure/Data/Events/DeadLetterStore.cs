using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Infrastructure.Data.Events;

public sealed class DeadLetterStore : IDeadLetterStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public DeadLetterStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task WriteAsync(DeadLetterRecord record, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.DeadLetters.Add(new DeadLetterRow
        {
            Origin = record.Origin.ToString(),
            Id = record.Id,
            Source = record.Source,
            EventId = record.EventId,
            Type = record.Type,
            Time = record.Time,
            SpecVersion = record.SpecVersion,
            Subject = record.Subject,
            DataContentType = record.DataContentType,
            Data = record.Data,
            ExtensionsJson = record.ExtensionsJson,
            FailingHandler = record.FailingHandler,
            ErrorMessage = record.ErrorMessage,
            ErrorStack = record.ErrorStack,
            AttemptCount = record.AttemptCount,
            DeadLetteredAt = record.DeadLetteredAt,
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DeadLetterRecord>> ListAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.DeadLetters.AsNoTracking()
            .OrderByDescending(e => e.DeadLetterId)
            .Take(limit)
            .OrderBy(e => e.DeadLetterId)
            .ToListAsync(ct);

        return rows.Select(ToRecord).ToList();
    }

    private static DeadLetterRecord ToRecord(DeadLetterRow row) =>
        new(
            Origin: ParseOrigin(row.Origin),
            Id: row.Id,
            Source: row.Source,
            EventId: row.EventId,
            Type: row.Type,
            Time: row.Time,
            SpecVersion: row.SpecVersion,
            Subject: row.Subject,
            DataContentType: row.DataContentType,
            Data: Clone(row.Data),
            ExtensionsJson: row.ExtensionsJson,
            FailingHandler: row.FailingHandler,
            ErrorMessage: row.ErrorMessage,
            ErrorStack: row.ErrorStack,
            AttemptCount: row.AttemptCount,
            DeadLetteredAt: row.DeadLetteredAt);

    private static EventOrigin ParseOrigin(string text) => text switch
    {
        "WorkflowRun" => EventOrigin.WorkflowRun,
        "Issue" => EventOrigin.Issue,
        "Epic" => EventOrigin.Epic,
        _ => throw new InvalidOperationException($"Unknown event origin '{text}'."),
    };

    private static JsonElement Clone(JsonElement data) =>
        JsonDocument.Parse(data.GetRawText()).RootElement.Clone();
}
