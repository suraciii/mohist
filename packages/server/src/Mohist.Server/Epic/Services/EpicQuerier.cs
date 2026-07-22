using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Domain;
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

    public async Task<List<EpicWithProgressDto>> ListAsync(
        string projectId,
        string? search = null,
        string? sortBy = null,
        string? sortDir = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var normalizedSearch = NormalizeSearch(search);
        var orderBy = ResolveOrderBy(sortBy, sortDir);

        var sql = $$"""
            SELECT
                e."ProjectId" AS "EpicProjectId",
                e."Number" AS "EpicNumber",
                e."Title" AS "EpicTitle",
                e."Description" AS "EpicDescription",
                e."Priority" AS "EpicPriority",
                e."Status" AS "EpicStatus",
                e."CreatedAt" AS "EpicCreatedAt",
                e."UpdatedAt" AS "EpicUpdatedAt",
                e."PauseReason" AS "EpicPauseReason",
                i."Number" AS "IssueNumber",
                i."Status" AS "IssueStatus",
                i."Title" AS "IssueTitle",
                i."Priority" AS "IssuePriority",
                i."IsDraft" AS "IssueIsDraft",
                i."PrerequisiteNumbersJson" AS "IssuePrerequisiteNumbersJson",
                i."IsArchived" AS "IssueIsArchived",
                i."Number" AS "IssueOrder"
            FROM "Epics" e
            LEFT JOIN "Issues" i ON i."ProjectId" = e."ProjectId" AND i."EpicNumber" = e."Number"
            WHERE e."ProjectId" = @projectId
            {{(normalizedSearch is null ? string.Empty : "AND LOWER(e.\"Title\") LIKE LOWER('%' || @search || '%') ESCAPE '\\'")}}
            ORDER BY {{orderBy}}
            """;

        var parameters = new List<SqliteParameter> { new("@projectId", projectId) };
        if (normalizedSearch is not null)
            parameters.Add(new SqliteParameter("@search", normalizedSearch));

        var rows = await db.Database
            .SqlQueryRaw<EpicIssueListItem>(sql, parameters.Cast<object>().ToArray())
            .ToListAsync();

        var allIssueRows = rows
            .Where(r => r.IssueNumber.HasValue && r.IssueIsArchived != true)
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
            if (currentEpic is null || currentEpic.EpicNumber != row.EpicNumber)
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

    // Order-by fragments are composed from a closed enum-bound map so the
    // sort selector never reaches the SQL builder as an interpolated string.
    // New keys / directions must be added here explicitly; unknown inputs
    // fall back to the original hardcoded ordering.
    internal const string DefaultOrderBy = "e.\"Priority\" ASC, e.\"UpdatedAt\" DESC, i.\"Number\"";

    private static readonly Dictionary<(string Field, string Dir), string> OrderByMap =
        new()
        {
            [("priority", "asc")] = "e.\"Priority\" ASC, e.\"UpdatedAt\" DESC, i.\"Number\"",
            [("priority", "desc")] = "e.\"Priority\" DESC, e.\"UpdatedAt\" DESC, i.\"Number\"",
            [("updated", "asc")] = "e.\"UpdatedAt\" ASC, e.\"Priority\" ASC, i.\"Number\"",
            [("updated", "desc")] = "e.\"UpdatedAt\" DESC, e.\"Priority\" ASC, i.\"Number\"",
        };

    internal static string ResolveOrderBy(string? sortBy, string? sortDir)
    {
        var field = NormalizeSortToken(sortBy);
        var dir = NormalizeSortToken(sortDir);
        if (field is null || dir is null) return DefaultOrderBy;
        return OrderByMap.TryGetValue((field, dir), out var fragment)
            ? fragment
            : DefaultOrderBy;
    }

    private static string? NormalizeSortToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.All(char.IsLetterOrDigit) ? trimmed.ToLowerInvariant() : null;
    }

    internal static string? NormalizeSearch(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : EscapeLikePattern(trimmed);
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_");

    public async Task<EpicDetailDto?> GetAsync(string projectId, int epicNumber)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var epic = await db.Epics.AsNoTracking().FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == epicNumber);
        return epic is null ? null : await ToDetailAsync(db, epic);
    }

    public async Task<EpicDetailDto?> GetByNumberAsync(string projectId, int number)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var epic = await db.Epics.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == number);
        return epic is null ? null : await ToDetailAsync(db, epic);
    }

    public async Task<int?> GetEpicNumberForIssueAsync(string projectId, int issueNumber)
    {
        if (string.IsNullOrWhiteSpace(projectId) || issueNumber <= 0)
            return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var activeOwner = await (
            from issue in db.Issues.AsNoTracking()
            join epic in db.Epics.AsNoTracking()
                on new { issue.ProjectId, EpicNumber = issue.EpicNumber } equals new { epic.ProjectId, EpicNumber = (int?)epic.Number }
            where issue.ProjectId == projectId
                && issue.Number == issueNumber
                && issue.EpicNumber != null
                && epic.ProjectId == projectId
                && epic.Status != EpicStatusName.Done
                && epic.Status != EpicStatusName.Closed
            select issue.EpicNumber
        ).FirstOrDefaultAsync();
        return activeOwner;
    }

    public async Task<List<int>> GetEpicNumbersDependentOnPrerequisiteAsync(
        string projectId,
        int prerequisiteNumber)
    {
        if (string.IsNullOrWhiteSpace(projectId) || prerequisiteNumber <= 0)
            return [];
        await using var db = await _dbFactory.CreateDbContextAsync();
        var projectIdParam = new SqliteParameter("@projectId", projectId);
        var prereqParam = new SqliteParameter("@prereqNumber", prerequisiteNumber);
        var issueNumberRows = await db.Database
            .SqlQueryRaw<int>(
                """
                SELECT "Number" FROM "Issues"
                WHERE "ProjectId" = @projectId
                  AND EXISTS (
                    SELECT 1 FROM json_each("PrerequisiteNumbersJson")
                    WHERE json_each.value = @prereqNumber
                  )
                """,
                projectIdParam, prereqParam)
            .ToListAsync();

        if (issueNumberRows.Count == 0) return [];

        var epicNumbers = await (
            from issue in db.Issues.AsNoTracking()
            join epic in db.Epics.AsNoTracking()
                on new { issue.ProjectId, EpicNumber = issue.EpicNumber } equals new { epic.ProjectId, EpicNumber = (int?)epic.Number }
            where issue.ProjectId == projectId
                && issue.Number != null
                && issueNumberRows.Contains(issue.Number.Value)
                && issue.EpicNumber != null
                && epic.ProjectId == projectId
                && epic.Status != EpicStatusName.Done
                && epic.Status != EpicStatusName.Closed
            select issue.EpicNumber.GetValueOrDefault()
        ).Distinct().ToListAsync();
        return epicNumbers;
    }

    private static EpicWithProgressDto BuildEpicWithProgress(
        EpicIssueListItem epicRow,
        List<EpicIssueListItem> rows,
        Dictionary<int, IssueReadModel> allIssuesByNumber,
        HashSet<int> allUndeliveredNumbers) =>
        new(
            epicRow.EpicProjectId,
            epicRow.EpicNumber!.Value,
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
            .Where(r => r.IssueNumber.HasValue && r.IssueIsArchived != true)
            .OrderBy(r => r.IssueOrder)
            .ToList();
        if (linkedRows.Count == 0) return [];

        var memberNumbers = new HashSet<int>(linkedRows.Select(r => r.IssueNumber!.Value));

        return linkedRows
            .Select(row =>
            {
                var issue = allIssuesByNumber[row.IssueNumber!.Value];
                var blocker = ComputeStartBlocker(issue, allUndeliveredNumbers);
                return new LinkedIssueDto(
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
        return new EpicDetailDto(epic.ProjectId, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), linked, progress, epic.PauseReason);
    }

    private async Task<List<LinkedIssueDto>> GetLinkedIssuesAsync(MohistDbContext db, EpicRow epic)
    {
        var memberNumbers = await db.Issues.AsNoTracking()
            .Where(issue => issue.ProjectId == epic.ProjectId && issue.EpicNumber == epic.Number && issue.Number != null)
            .OrderBy(issue => issue.Number)
            .Select(issue => issue.Number!.Value)
            .ToListAsync();
        if (memberNumbers.Count == 0) return [];

        var allIssues = await _issuesQuery.ListReadModelsAsync(epic.ProjectId, all: true);
        var byNumber = allIssues.ToDictionary(i => i.Number);
        var memberNumberSet = memberNumbers.ToHashSet();
        return memberNumbers
            .Select(number => byNumber.TryGetValue(number, out var issue)
                ? new LinkedIssueDto(
                    Number: issue.Number,
                    Title: issue.Title,
                    Status: issue.Status,
                    Stage: issue.WorkflowStage ?? "",
                    Health: issue.Health,
                    Priority: issue.Priority,
                    CanStart: issue.CanStart,
                    StartBlocker: issue.Blocker,
                    PrerequisiteNumbers: issue.PrerequisiteNumbers,
                    ExternalPrerequisites: BuildExternalPrerequisites(issue, memberNumberSet, byNumber))
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
        public string EpicProjectId { get; set; } = "";
        public int? EpicNumber { get; set; }
        public string EpicTitle { get; set; } = "";
        public string EpicDescription { get; set; } = "";
        public string EpicPriority { get; set; } = "";
        public string EpicStatus { get; set; } = "";
        public DateTimeOffset EpicCreatedAt { get; set; }
        public DateTimeOffset EpicUpdatedAt { get; set; }
        public string? EpicPauseReason { get; set; }
        public int? IssueNumber { get; set; }
        public string? IssueStatus { get; set; }
        public string? IssueTitle { get; set; }
        public string? IssuePriority { get; set; }
        public bool? IssueIsDraft { get; set; }
        public string? IssuePrerequisiteNumbersJson { get; set; }
        public bool? IssueIsArchived { get; set; }
        public int? IssueOrder { get; set; }
    }
}
