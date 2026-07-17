using Mohist.Server.Epic.Domain;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Epic.Services;

public static class EpicProgress
{
    public static readonly IReadOnlyList<string> TerminalStatuses = new[] { "done", "closed" };

    private const string HealthBlocked = "blocked";

    public static EpicProgressDto Build(IReadOnlyList<LinkedIssueDto> linked)
    {
        var completed = linked.Where(IsCompleted).ToList();
        var open = linked.Where(IsOpen).ToList();

        var next = SelectStartableNext(open);
        var nextIssueReason = next is null ? BuildNextIssueReason(open) : null;

        var inProgress = open.Where(i => i.Status == "in_progress").ToList();
        var blocked = inProgress
            .Where(i => string.Equals(i.Health, HealthBlocked, StringComparison.Ordinal))
            .Select(ToProgressIssue)
            .ToArray();
        var active = inProgress
            .Where(i => !string.Equals(i.Health, HealthBlocked, StringComparison.Ordinal))
            .Select(ToProgressIssue)
            .ToArray();

        return new EpicProgressDto(
            completed.Count,
            linked.Count,
            blocked,
            active,
            next is null ? null : new EpicNextIssueDto(next.Number, next.Title),
            nextIssueReason,
            IsReadyToComplete(linked));
    }

    public static bool IsCompleted(LinkedIssueDto issue) => issue.Status is "done" or "completed";

    /// <summary>
    /// A linked issue is terminal when it has no remaining execution:
    /// delivered (<c>done</c>/<c>completed</c>) or cancelled
    /// (<c>cancelled</c>). Terminal issues neither block readiness nor
    /// count toward <c>deliveredCount</c> when cancelled.
    /// </summary>
    public static bool IsTerminal(LinkedIssueDto issue) => IsCompleted(issue) || issue.Status == "cancelled";

    /// <summary>
    /// An open linked issue is anything still capable of advancing the
    /// product goal: backlog, draft, in-progress, blocked, paused, etc.
    /// Used by the readiness rule, the next-issue selection, and the
    /// advancement path.
    /// </summary>
    public static bool IsOpen(LinkedIssueDto issue) => !IsTerminal(issue);

    /// <summary>
    /// The single source of truth for whether an epic may transition to
    /// <c>done</c>. An epic is ready to complete when it has at least
    /// one linked issue AND every linked issue is terminal. A
    /// <c>cancelled</c> remaining issue does NOT keep readiness false.
    /// </summary>
    public static bool IsReadyToComplete(IReadOnlyList<LinkedIssueDto> linked) =>
        linked.Count > 0 && !linked.Any(IsOpen);

    public static bool IsTerminal(EpicStatus status) => status is EpicStatus.Done or EpicStatus.Closed;

    public static bool IsTerminal(string status) => TerminalStatuses.Contains(status);

    /// <summary>
    /// Shared next-issue selection used by both the read-model
    /// (<see cref="Build"/>) and the autonomous-progression path on
    /// <c>EpicGrain.TryStartNext</c>. Returns the highest-priority
    /// <c>CanStart &amp;&amp; StartBlocker is null</c> open
    /// issue, or <c>null</c> if any linked issue is currently
    /// <c>in_progress</c> (serial slot occupied) or no candidate
    /// matches. <c>cancelled</c> issues are excluded by the
    /// <c>open</c> filter the callers pass in.
    /// </summary>
    public static LinkedIssueDto? SelectStartableNext(IReadOnlyList<LinkedIssueDto> open)
    {
        if (open.Any(i => i.Status == "in_progress"))
            return null;

        return open
            .Where(i => i.Status != "in_progress" && i.CanStart && i.StartBlocker is null)
            .OrderBy(i => PriorityRank(i.Priority))
            .ThenBy(i => i.Number)
            .Cast<LinkedIssueDto?>()
            .FirstOrDefault();
    }

    private static string? BuildNextIssueReason(IReadOnlyList<LinkedIssueDto> open)
    {
        if (open.Count == 0) return null;

        var running = open
            .OrderBy(i => i.Number)
            .FirstOrDefault(i => i.Status == "in_progress");
        if (running is not null)
            return $"Waiting for #{running.Number} to complete";

        var blocker = open
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
        new(i.Number, i.Title, i.Health);

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
