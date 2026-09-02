using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api;

internal static class TaskLogRequestValidator
{
    internal static (List<TaskLogLine> Lines, string? Error) BuildValidatedLines(
        IReadOnlyList<TaskLogUploadEntry> entries)
    {
        var lines = new List<TaskLogLine>(entries.Count);
        long previousSeq = 0;
        var totalTextLength = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Seq <= 0)
                return (lines, $"Entry {i} seq must be positive");
            if (entry.Seq <= previousSeq)
                return (lines, "Entry seq values must be strictly increasing");
            if (entry.Timestamp == default)
                return (lines, $"Entry {i} timestamp must be provided");
            if (string.IsNullOrWhiteSpace(entry.Source))
                return (lines, $"Entry {i} source must be provided");
            if (entry.Source.Length > TaskLogUploadLimits.MaxSourceLength)
                return (lines, $"Entry {i} source exceeds {TaskLogUploadLimits.MaxSourceLength} characters");
            if (entry.Text is null)
                return (lines, $"Entry {i} text must be provided");
            if (entry.Text.Length > TaskLogUploadLimits.MaxTextLength)
                return (lines, $"Entry {i} text exceeds {TaskLogUploadLimits.MaxTextLength} characters");

            totalTextLength += entry.Text.Length;
            if (totalTextLength > TaskLogUploadLimits.MaxTotalTextLength)
                return (lines, $"Task-log text payload exceeds {TaskLogUploadLimits.MaxTotalTextLength} characters");

            lines.Add(new TaskLogLine(entry.Seq, entry.Timestamp, entry.Source, entry.Text));
            previousSeq = entry.Seq;
        }

        return (lines, null);
    }
}
