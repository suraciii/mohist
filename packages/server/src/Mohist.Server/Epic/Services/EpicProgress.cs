using Mohist.Server.Epic.Domain;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Epic.Services;

public static class EpicProgress
{
    public static readonly IReadOnlyList<string> TerminalStatuses = new[] { "done", "closed" };

    private const string HealthBlocked = "blocked";
    private const string HealthActive = "active";

    public static EpicProgressDto Build(IReadOnlyList<LinkedIssueDto> linked)
    {
        var completed = linked.Where(IsCompleted).ToList();
        var undelivered = linked.Where(i => !IsCompleted(i)).ToList();

        var next = SelectStartableNext(undelivered);
        var nextIssueReason = next is null ? BuildNextIssueReason(undelivered) : null;

        var blocked = undelivered
            .Where(i => string.Equals(i.Health, HealthBlocked, StringComparison.Ordinal))
            .Select(ToProgressIssue)
            .ToArray();
        var active = undelivered
            .Where(i => string.Equals(i.Health, HealthActive, StringComparison.Ordinal))
            .Select(ToProgressIssue)
            .ToArray();

        return new EpicProgressDto(
            completed.Count,
            linked.Count,
            blocked,
            active,
            next is null ? null : new EpicNextIssueDto(next.Id, next.Number, next.Title),
            nextIssueReason,
            linked.Count > 0 && completed.Count == linked.Count);
    }

    public static bool IsCompleted(LinkedIssueDto issue) => issue.Status is "done" or "completed";

    public static bool IsTerminal(EpicStatus status) => status is EpicStatus.Done or EpicStatus.Closed;

    public static bool IsTerminal(string status) => TerminalStatuses.Contains(status);

    private static LinkedIssueDto? SelectStartableNext(IReadOnlyList<LinkedIssueDto> undelivered)
    {
        return undelivered
            .Where(i => i.CanStart && i.StartBlocker is null)
            .OrderBy(i => PriorityRank(i.Priority))
            .ThenBy(i => i.Number)
            .Cast<LinkedIssueDto?>()
            .FirstOrDefault();
    }

    private static string? BuildNextIssueReason(IReadOnlyList<LinkedIssueDto> undelivered)
    {
        if (undelivered.Count == 0) return null;
        var blocker = undelivered
            .OrderBy(i => PriorityRank(i.Priority))
            .ThenBy(i => i.Number)
            .Select(i => ReasonFor(i))
            .FirstOrDefault(r => r is not null);
        return blocker;
    }

    private static string? ReasonFor(LinkedIssueDto issue)
    {
        if (issue.StartBlocker is null) return null;
        return issue.StartBlocker switch
        {
            IssueStartBlockerDto.DraftBlocker => $"Still a draft: #{issue.Number}",
            IssueStartBlockerDto.WaitingForBlocker waiting when waiting.Issue is { Number: var n } =>
                $"Waiting on #{n}",
            _ => $"Blocked: #{issue.Number}",
        };
    }

    private static EpicProgressIssueDto ToProgressIssue(LinkedIssueDto i) =>
        new(i.Id, i.Number, i.Title, i.Health);

    private static int PriorityRank(string? priority) => priority switch
    {
        "p0" => 0,
        "p1" => 1,
        "p2" => 2,
        "p3" => 3,
        "p4" => 4,
        _ => 9,
    };
}