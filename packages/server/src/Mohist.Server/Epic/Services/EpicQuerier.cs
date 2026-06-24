using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;

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
        var rows = await db.Epics.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .ToListAsync();
        rows = OrderByPriorityThenUpdatedAt(rows).ToList();
        var result = new List<EpicWithProgressDto>();
        foreach (var epic in rows)
            result.Add(await ToWithProgressAsync(db, epic));
        return result;
    }

    private static IEnumerable<EpicRow> OrderByPriorityThenUpdatedAt(IEnumerable<EpicRow> epics) =>
        epics
            .OrderBy(e => PriorityRank(e.Priority))
            .ThenByDescending(e => e.UpdatedAt);

    internal static int PriorityRank(string? priority) => priority switch
    {
        "p0" => 0,
        "p1" => 1,
        "p2" => 2,
        "p3" => 3,
        "p4" => 4,
        _ => 9,
    };

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

    private async Task<EpicWithProgressDto> ToWithProgressAsync(MohistDbContext db, EpicRow epic)
    {
        var progress = await BuildProgressAsync(db, epic);
        return new EpicWithProgressDto(epic.Id, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), progress, epic.PauseReason);
    }

    private async Task<EpicDetailDto> ToDetailAsync(MohistDbContext db, EpicRow epic)
    {
        var linked = await GetLinkedIssuesAsync(db, epic);
        var progress = BuildProgress(linked);
        return new EpicDetailDto(epic.Id, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), linked, progress, epic.PauseReason);
    }

    private async Task<EpicProgressDto> BuildProgressAsync(MohistDbContext db, EpicRow epic) =>
        BuildProgress(await GetLinkedIssuesAsync(db, epic));

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
}
