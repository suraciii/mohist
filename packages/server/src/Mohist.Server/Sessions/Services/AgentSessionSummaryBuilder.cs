using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Builds a compact summary of prior session context for use as the initial
/// prompt when a session is recovered via <c>CompactAsync</c>. The summary
/// preserves task instructions, key tool/decision signals, and recorded
/// failure context while keeping the total length bounded.
/// </summary>
public static class AgentSessionSummaryBuilder
{
    public const int DefaultMaxChars = 4_000;

    public static async Task<string> BuildAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId,
        int? maxChars = null,
        CancellationToken ct = default)
    {
        var budget = maxChars is > 0 ? maxChars.Value : DefaultMaxChars;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .OrderBy(t => t.Sequence)
            .ToListAsync(ct);
        if (turns.Count == 0)
            return BuildEmptySummary();

        var turnIds = turns.Select(t => t.Id).ToArray();
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(p => turnIds.Contains(p.TurnId))
            .OrderBy(p => p.Sequence)
            .ToListAsync(ct);
        var partsByTurn = parts
            .GroupBy(p => p.TurnId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Sequence).ToList());

        var builder = new StringBuilder();
        builder.AppendLine("## Recovery summary");
        builder.AppendLine("Previous session context was compacted. The notes below preserve the task instruction and key observations from the prior run.");

        var failure = ExtractFailure(parts);
        if (!string.IsNullOrWhiteSpace(failure))
        {
            builder.AppendLine().AppendLine("### Prior failure").Append(failure);
        }

        var firstTaskPrompt = turns
            .Select(t => t.PromptText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(firstTaskPrompt))
        {
            builder.AppendLine().AppendLine("### Original task").Append(Trim(firstTaskPrompt!, 1_500));
        }

        var keyDecisions = ExtractKeyDecisions(parts);
        if (keyDecisions.Count > 0)
        {
            builder.AppendLine().AppendLine("### Key observations");
            foreach (var line in keyDecisions)
                builder.Append("- ").AppendLine(line);
        }

        return Trim(builder.ToString(), budget);
    }

    private static string BuildEmptySummary() =>
        "## Recovery summary\nNo prior context was preserved for this session.";

    private static string? ExtractFailure(List<AgentSessionTranscriptPartRow> parts)
    {
        var closed = parts.LastOrDefault(p => p.Type == TranscriptPartTypes.SessionActivity);
        if (closed is null) return null;
        try
        {
            var payload = JSON.DeserializeElement(closed.PayloadJson);
            if (payload.ValueKind != JsonValueKind.Object) return null;
            var status = AgentSessionJsonHelper.GetStringProp(payload, "status");
            var reason = AgentSessionJsonHelper.GetStringProp(payload, "failureReason");
            var category = AgentSessionJsonHelper.GetStringProp(payload, "failureCategory");
            if (string.IsNullOrWhiteSpace(reason) && string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(status))
                return null;
            var text = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(status)) text.Append("status=").Append(status);
            if (!string.IsNullOrWhiteSpace(category)) text.Append(", category=").Append(category);
            if (!string.IsNullOrWhiteSpace(reason)) text.Append(": ").Append(reason);
            return text.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ExtractKeyDecisions(List<AgentSessionTranscriptPartRow> parts)
    {
        var lines = new List<string>();
        foreach (var part in parts)
        {
            if (part.Type == "tool")
            {
                var title = TryReadTitle(part);
                var status = TryReadStatus(part);
                if (!string.IsNullOrWhiteSpace(title))
                    lines.Add($"tool {title}{(string.IsNullOrWhiteSpace(status) ? string.Empty : $" ({status})")}");
            }
            else if (part.Type == TranscriptPartTypes.SessionActivity)
            {
                var text = AgentSessionJsonHelper.ExtractText(part.PayloadJson);
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add($"closed: {Trim(text, 200)}");
            }
        }
        if (lines.Count == 0) return lines;
        // Keep the most recent N lines and prefer diversity of tool actions.
        return lines
            .GroupBy(line => line, StringComparer.Ordinal)
            .Select(g => g.First())
            .Reverse()
            .Take(12)
            .Reverse()
            .ToList();
    }

    private static string? TryReadTitle(AgentSessionTranscriptPartRow part)
    {
        try
        {
            var payload = JSON.DeserializeElement(part.PayloadJson);
            if (payload.ValueKind != JsonValueKind.Object) return null;
            return AgentSessionJsonHelper.GetStringProp(payload, "title")
                ?? AgentSessionJsonHelper.GetStringProp(payload, "toolName");
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadStatus(AgentSessionTranscriptPartRow part)
    {
        try
        {
            var payload = JSON.DeserializeElement(part.PayloadJson);
            if (payload.ValueKind != JsonValueKind.Object) return null;
            return AgentSessionJsonHelper.GetStringProp(payload, "status")
                ?? AgentSessionJsonHelper.GetStringProp(payload, "state");
        }
        catch
        {
            return null;
        }
    }

    private static string Trim(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return text[..(max - 1)] + "\u2026";
    }
}
