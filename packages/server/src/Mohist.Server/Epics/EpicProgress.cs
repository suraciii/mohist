namespace Mohist.Server.Epics;

public static class EpicProgress
{
    public static readonly IReadOnlyList<string> TerminalStatuses = new[] { "done", "closed" };

    public static EpicProgressDto Build(IReadOnlyList<LinkedIssueDto> linked)
    {
        var completed = linked.Where(IsCompleted).ToList();
        var next = linked.FirstOrDefault(i => !IsCompleted(i));
        return new EpicProgressDto(
            completed.Count,
            linked.Count,
            linked.Where(i => i.Status == "blocked").Select(i => i.Id).ToArray(),
            linked.Where(i => i.Status == "active" && !IsCompleted(i)).Select(i => i.Id).ToArray(),
            next is null ? null : new EpicNextIssueDto(next.Id, next.Number, next.Title),
            linked.Count > 0 && completed.Count == linked.Count);
    }

    public static bool IsCompleted(LinkedIssueDto issue) => issue.Status is "done" or "completed";

    public static bool IsTerminal(string status) => TerminalStatuses.Contains(status);
}
