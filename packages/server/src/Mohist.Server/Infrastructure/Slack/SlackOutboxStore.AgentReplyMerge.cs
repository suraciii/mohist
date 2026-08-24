using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;

namespace Mohist.Server.Infrastructure.Slack;

public sealed partial class SlackOutboxStore
{
    private async Task<SlackOutboxRow> MergeReplyTerminalAsync(
        MohistDbContext db,
        SlackOutboxRow terminal,
        string redactedText,
        bool idempotentRetry,
        CancellationToken ct)
    {
        var previous = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        var previousText = !string.IsNullOrWhiteSpace(previous.FallbackText)
            ? previous.FallbackText
            : previous.Text;
        if (idempotentRetry && string.Equals(previousText, redactedText, StringComparison.Ordinal))
            return terminal;
        var combined = string.IsNullOrWhiteSpace(previousText)
            ? redactedText
            : previousText + "\n\n" + redactedText;
        var segments = SlackFinalReplyRenderer.SegmentReplyText(combined);
        terminal.PayloadJson = JsonSerializer.Serialize(previous with
        {
            Text = combined,
            Segments = segments.Count > 1 ? segments : null,
        });
        terminal.State = SlackOutboxStates.Pending;
        terminal.NextAttemptAt = _timeProvider.GetUtcNow();
        terminal.ClaimedAt = null;
        terminal.ClaimedByAdapterId = null;
        terminal.DeliveryUncertainAt = null;
        terminal.LastError = null;
        terminal.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return terminal;
    }
}
