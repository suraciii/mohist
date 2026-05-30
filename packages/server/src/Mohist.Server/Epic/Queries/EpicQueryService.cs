using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epics;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Epic.Queries;

public class EpicQueryService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueQueryService _issuesQuery;

    public EpicQueryService(IDbContextFactory<MohistDbContext> dbFactory, IssueQueryService issuesQuery)
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
        rows = rows.OrderBy(e => e.CreatedAt).ToList();
        var result = new List<EpicWithProgressDto>();
        foreach (var epic in rows)
            result.Add(await ToWithProgressAsync(db, epic));
        return result;
    }

    public async Task<EpicDetailDto?> GetAsync(string projectId, string epicId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var epic = await db.Epics.AsNoTracking().FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        return epic is null ? null : await ToDetailAsync(db, epic);
    }

    private async Task<EpicWithProgressDto> ToWithProgressAsync(MohistDbContext db, EpicEntry epic)
    {
        var progress = await BuildProgressAsync(db, epic);
        return new EpicWithProgressDto(epic.Id, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), progress);
    }

    private async Task<EpicDetailDto> ToDetailAsync(MohistDbContext db, EpicEntry epic)
    {
        var linked = await GetLinkedIssuesAsync(db, epic);
        var progress = BuildProgress(linked);
        return new EpicDetailDto(epic.Id, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), linked, progress);
    }

    private async Task<EpicProgressDto> BuildProgressAsync(MohistDbContext db, EpicEntry epic) =>
        BuildProgress(await GetLinkedIssuesAsync(db, epic));

    private async Task<List<LinkedIssueDto>> GetLinkedIssuesAsync(MohistDbContext db, EpicEntry epic)
    {
        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == epic.ProjectId && link.EpicId == epic.Id)
            .ToListAsync();
        links = links.OrderBy(link => link.CreatedAt).ToList();
        if (links.Count == 0) return [];

        var allIssues = await _issuesQuery.ListAsync(epic.ProjectId, all: true);
        var byId = allIssues.ToDictionary(i => i.Id);
        return links
            .Select(link => byId.TryGetValue(link.IssueId, out var issue) ? new LinkedIssueDto(issue.Id, issue.Number, issue.Title, issue.RuntimeStatus, issue.Stage, issue.Priority) : null)
            .Where(i => i is not null)
            .Cast<LinkedIssueDto>()
            .ToList();
    }

    private static EpicProgressDto BuildProgress(IReadOnlyList<LinkedIssueDto> linked)
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

    private static bool IsCompleted(LinkedIssueDto issue) => issue.Stage == "done" || issue.Status is "done" or "completed";
}
