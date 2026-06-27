using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using System.Text.Json;

namespace Mohist.Server.Epic.Services;

public class EpicQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueQuerier _issuesQuery;

    public EpicQuerier(IDbContextFactory<MohistDbContext> dbFactory, IssueQuerier issuesQuery)
    {
        _dbFactory = dbFactory;
        _issuesQuery = issuesQuery;
    }

    public async Task<List<EpicWithProgressDto>> ListAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        const string sql = """
            SELECT
                e."Id" AS "EpicId",
                e."Number" AS "EpicNumber",
                e."Title" AS "EpicTitle",
                e."Description" AS "EpicDescription",
                e."Priority" AS "EpicPriority",
                e."Status" AS "EpicStatus",
                e."CreatedAt" AS "EpicCreatedAt",
                e."UpdatedAt" AS "EpicUpdatedAt",
                e."PauseReason" AS "EpicPauseReason",
                li."IssueId" AS "IssueId",
                i."Number" AS "IssueNumber",
                i."Status" AS "IssueStatus",
                i."Title" AS "IssueTitle",
                i."Priority" AS "IssuePriority",
                i."IsDraft" AS "IssueIsDraft",
                i."PrerequisiteNumbersJson" AS "IssuePrerequisiteNumbersJson",
                i."IsArchived" AS "IssueIsArchived",
                li."CreatedAt" AS "LinkCreatedAt"
            FROM "Epics" e
            LEFT JOIN "EpicIssues" li ON li."EpicId" = e."Id"
            LEFT JOIN "Issues" i ON i."IssueId" = li."IssueId"
            WHERE e."ProjectId" = @projectId
            ORDER BY e."Priority", e."UpdatedAt" DESC, li."CreatedAt"
            """;

        var rows = await db.Database
            .SqlQueryRaw<EpicIssueListItem>(sql, new SqliteParameter("@projectId", projectId))
            .ToListAsync();

        var allIssueRows = rows
            .Where(r => !string.IsNullOrEmpty(r.IssueId) && r.IssueNumber.HasValue && r.IssueIsArchived != true)
            .ToList();
        var allIssuesByNumber = allIssueRows
            .DistinctBy(r => r.IssueNumber!.Value)
            .ToDictionary(
                r => r.IssueNumber!.Value,
                r => ToIssueReadModel(r));
        var allUndeliveredNumbers = new HashSet<int>(
            allIssuesByNumber.Values
                .Where(i => i.Status is not ("done" or "completed"))
                .Select(i => i.Number));

        var result = new List<EpicWithProgressDto>();
        List<EpicIssueListItem>? currentEpicRows = null;
        EpicIssueListItem? currentEpic = null;
        foreach (var row in rows)
        {
            if (currentEpic is null || currentEpic.EpicId != row.EpicId)
            {
                if (currentEpic is not null)
                    result.Add(BuildEpicWithProgress(currentEpic, currentEpicRows!, allIssuesByNumber, allUndeliveredNumbers));
                currentEpic = row;
                currentEpicRows = [row];
            }
            else
            {
                currentEpicRows!.Add(row);
            }
        }
        if (currentEpic is not null)
            result.Add(BuildEpicWithProgress(currentEpic, currentEpicRows!, allIssuesByNumber, allUndeliveredNumbers));

        return result;
    }

    public async Task<EpicDetailDto?> GetAsync(string projectId, string epicId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var epic = await db.Epics.AsNoTracking().FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        return epic is null ? null : await ToDetailAsync(db, epic);
    }

    public async Task<EpicDetailDto?> GetByNumberAsync(string projectId, int number)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var epic = await db.Epics.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == number);
        return epic is null ? null : await ToDetailAsync(db, epic);
    }

    public async Task<string?> GetEpicIdForIssueAsync(string projectId, string issueId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(issueId))
            return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var link = await db.EpicIssues.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ProjectId == projectId && l.IssueId == issueId);
        return link?.EpicId;
    }

    private static EpicWithProgressDto BuildEpicWithProgress(
        EpicIssueListItem epicRow,
        List<EpicIssueListItem> rows,
        Dictionary<int, IssueReadModel> allIssuesByNumber,
        HashSet<int> allUndeliveredNumbers) =>
        new(
            epicRow.EpicId,
            epicRow.EpicNumber,
            epicRow.EpicTitle,
            epicRow.EpicDescription,
            epicRow.EpicPriority,
            epicRow.EpicStatus,
            epicRow.EpicCreatedAt.ToString("o"),
            epicRow.EpicUpdatedAt.ToString("o"),
            BuildProgress(BuildLinkedIssuesFromRows(rows, allIssuesByNumber, allUndeliveredNumbers)),
            epicRow.EpicPauseReason);

    private static List<LinkedIssueDto> BuildLinkedIssuesFromRows(
        List<EpicIssueListItem> rows,
        Dictionary<int, IssueReadModel> allIssuesByNumber,
        HashSet<int> allUndeliveredNumbers)
    {
        var linkedRows = rows
            .Where(r => !string.IsNullOrEmpty(r.IssueId) && r.IssueNumber.HasValue && r.IssueIsArchived != true)
            .OrderBy(r => r.LinkCreatedAt)
            .ToList();
        if (linkedRows.Count == 0) return [];

        var memberNumbers = new HashSet<int>(linkedRows.Select(r => r.IssueNumber!.Value));

        return linkedRows
            .Select(row =>
            {
                var issue = allIssuesByNumber[row.IssueNumber!.Value];
                var blocker = ComputeStartBlocker(issue, allUndeliveredNumbers);
                return new LinkedIssueDto(
                    Id: issue.Id,
                    Number: issue.Number,
                    Title: issue.Title,
                    Status: issue.Status,
                    Stage: "",
                    Health: ListPathHealth(issue.Status),
                    Priority: issue.Priority,
                    CanStart: blocker is null,
                    StartBlocker: blocker,
                    PrerequisiteNumbers: issue.PrerequisiteNumbers,
                    ExternalPrerequisites: BuildExternalPrerequisites(issue, memberNumbers, allIssuesByNumber));
            })
            .ToList();
    }

    private static IssueReadModel ToIssueReadModel(EpicIssueListItem row)
    {
        var status = CanonicalIssueStatus(row.IssueStatus);
        return new()
        {
            Id = row.IssueId!,
            Number = row.IssueNumber!.Value,
            Title = row.IssueTitle ?? "",
            Status = status,
            Health = ListPathHealth(status),
            Priority = row.IssuePriority ?? "p2",
            IsDraft = row.IssueIsDraft ?? false,
            WorkflowStage = status,
            PrerequisiteNumbers = ParsePrerequisiteNumbers(row.IssuePrerequisiteNumbersJson),
        };
    }

    private static string CanonicalIssueStatus(string? raw) =>
        Enum.TryParse<IssueStatus>(raw, ignoreCase: true, out var status)
            ? MohistDefaultWorkflowProjection.IssueStatusName(status)
            : raw ?? "backlog";

    private static int[] ParsePrerequisiteNumbers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<int[]>(json, JSON.Options) ?? []; }
        catch { return []; }
    }

    private static string ListPathHealth(string status) =>
        status == "in_progress" ? "active" : status;

    private static IssueStartBlockerDto? ComputeStartBlocker(IssueReadModel issue, HashSet<int> undeliveredPrerequisiteNumbers)
    {
        if (issue.IsDraft) return new IssueStartBlockerDto.DraftBlocker();
        foreach (var number in issue.PrerequisiteNumbers)
        {
            if (undeliveredPrerequisiteNumbers.Contains(number))
                return new IssueStartBlockerDto.WaitingForBlocker { Issue = new IssuePrerequisiteRefDto { Number = number } };
        }
        return null;
    }

    private async Task<EpicDetailDto> ToDetailAsync(MohistDbContext db, EpicRow epic)
    {
        var linked = await GetLinkedIssuesAsync(db, epic);
        var progress = BuildProgress(linked);
        return new EpicDetailDto(epic.Id, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), linked, progress, epic.PauseReason);
    }

    private async Task<List<LinkedIssueDto>> GetLinkedIssuesAsync(MohistDbContext db, EpicRow epic)
    {
        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == epic.ProjectId && link.EpicId == epic.Id)
            .ToListAsync();
        links = links.OrderBy(link => link.CreatedAt).ToList();
        if (links.Count == 0) return [];

        var allIssues = await _issuesQuery.ListAsync(epic.ProjectId, all: true);
        var byId = allIssues.ToDictionary(i => i.Id);
        var byNumber = allIssues.ToDictionary(i => i.Number);
        var memberNumbers = new HashSet<int>(
            links.Select(link => byId.TryGetValue(link.IssueId, out var member) ? member.Number : 0)
                .Where(n => n != 0));
        return links
            .Select(link => byId.TryGetValue(link.IssueId, out var issue)
                ? new LinkedIssueDto(
                    Id: issue.Id,
                    Number: issue.Number,
                    Title: issue.Title,
                    Status: issue.Status,
                    Stage: issue.WorkflowStage ?? "",
                    Health: issue.Health,
                    Priority: issue.Priority,
                    CanStart: issue.CanStart,
                    StartBlocker: issue.Blocker,
                    PrerequisiteNumbers: issue.PrerequisiteNumbers,
                    ExternalPrerequisites: BuildExternalPrerequisites(issue, memberNumbers, byNumber))
                : null)
            .Where(i => i is not null)
            .Cast<LinkedIssueDto>()
            .ToList();
    }

    internal static IReadOnlyList<IssuePrerequisiteRefDto> BuildExternalPrerequisites(
        IssueReadModel issue,
        HashSet<int> memberNumbers,
        Dictionary<int, IssueReadModel> byNumber)
    {
        if (issue.PrerequisiteNumbers.Length == 0) return [];
        var result = new List<IssuePrerequisiteRefDto>(issue.PrerequisiteNumbers.Length);
        var seen = new HashSet<int>();
        foreach (var prereqNumber in issue.PrerequisiteNumbers)
        {
            if (!seen.Add(prereqNumber)) continue;
            if (memberNumbers.Contains(prereqNumber)) continue;
            result.Add(byNumber.TryGetValue(prereqNumber, out var prereq)
                ? IssuePrerequisiteRefDto.FromSummary(IssuePrerequisiteSummary.FromReadModel(prereq))
                : new IssuePrerequisiteRefDto { Number = prereqNumber });
        }
        return result;
    }

    private static EpicProgressDto BuildProgress(IReadOnlyList<LinkedIssueDto> linked) =>
        EpicProgress.Build(linked);

    private sealed class EpicIssueListItem
    {
        public string EpicId { get; set; } = "";
        public int? EpicNumber { get; set; }
        public string EpicTitle { get; set; } = "";
        public string EpicDescription { get; set; } = "";
        public string EpicPriority { get; set; } = "";
        public string EpicStatus { get; set; } = "";
        public DateTimeOffset EpicCreatedAt { get; set; }
        public DateTimeOffset EpicUpdatedAt { get; set; }
        public string? EpicPauseReason { get; set; }
        public string? IssueId { get; set; }
        public int? IssueNumber { get; set; }
        public string? IssueStatus { get; set; }
        public string? IssueTitle { get; set; }
        public string? IssuePriority { get; set; }
        public bool? IssueIsDraft { get; set; }
        public string? IssuePrerequisiteNumbersJson { get; set; }
        public bool? IssueIsArchived { get; set; }
        public DateTimeOffset? LinkCreatedAt { get; set; }
    }
}
