using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epics;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Querying;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Issue.WorkflowProfiles;
using IssueDomain = Mohist.Server.Issue.Domain;

namespace Mohist.Server.Epic.Grains;

public class EpicGrain : Grain, IEpicGrain
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;

    public EpicGrain(IDbContextFactory<MohistDbContext> dbFactory, IGrainFactory grains)
    {
        _dbFactory = dbFactory;
        _grains = grains;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public async Task<EpicDto> CreateAsync(string projectId, string title, string? description, string? priority)
    {
        var number = await _grains.GetGrain<IEpicCounterGrain>(Mohist.Server.Infrastructure.Orleans.GrainKey.EpicCounter(projectId)).NextAsync();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var epic = new EpicRow
        {
            Id = $"epic_{Guid.NewGuid():N}",
            ProjectId = projectId,
            Number = number,
            Title = title,
            Description = description ?? "",
            Priority = string.IsNullOrWhiteSpace(priority) ? "p2" : priority,
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Epics.Add(epic);
        await db.SaveChangesAsync();
        return ToDto(epic);
    }

    public async Task LinkIssueAsync(string issueId, int issueNumber, string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];

        var epic = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (epic is null) throw new InvalidOperationException($"Epic {epicId} not found");

        var existing = await db.EpicIssues.AsNoTracking()
            .FirstOrDefaultAsync(link => link.ProjectId == projectId && link.IssueId == issueId);
        if (existing is not null && existing.EpicId != epicId)
        {
            var existingEpic = await db.Epics.AsNoTracking()
                .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == existing.EpicId);
            throw new InvalidOperationException($"Issue already belongs to Epic '{existing.EpicId}'{(existingEpic is not null ? $" ({existingEpic.Title})" : "")}");
        }

        if (existing is null)
        {
            db.EpicIssues.Add(new EpicIssueRow
            {
                EpicId = epicId,
                ProjectId = projectId,
                IssueId = issueId,
                IssueNumber = issueNumber,
            });
            epic.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task UnlinkIssueAsync(string issueId, string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];

        var row = await db.EpicIssues.FirstOrDefaultAsync(
            link => link.ProjectId == projectId && link.EpicId == epicId && link.IssueId == issueId);
        if (row is not null)
        {
            db.EpicIssues.Remove(row);
            var epic = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
            if (epic is not null) epic.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task<EpicDto> SetStatusAsync(string status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];
        var projectId = parts[0];

        var epic = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (epic is null) throw new InvalidOperationException($"Epic {epicId} not found");

        if (EpicProgress.IsTerminal(epic.Status))
            throw new EpicAlreadyTerminalException(epic.Status, status);

        if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
        {
            var ready = await IsReadyToMarkDoneAsync(db, projectId, epicId);
            if (!ready)
                throw new EpicNotReadyToMarkDoneException(epicId, await CountUndeliveredAsync(db, projectId, epicId));
        }

        if (string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase))
        {
            var links = await db.EpicIssues
                .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                .ToListAsync();
            if (links.Count > 0)
                db.EpicIssues.RemoveRange(links);
        }

        epic.Status = status;
        epic.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return ToDto(epic);
    }

    private async Task<bool> IsReadyToMarkDoneAsync(MohistDbContext db, string projectId, string epicId)
    {
        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .ToListAsync();
        if (links.Count == 0) return false;
        var linked = await BuildLinkedIssueDtosAsync(db, projectId, links);
        var progress = EpicProgress.Build(linked);
        return progress.ReadyToMarkDone;
    }

    private async Task<int> CountUndeliveredAsync(MohistDbContext db, string projectId, string epicId)
    {
        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .ToListAsync();
        if (links.Count == 0) return 0;
        var linked = await BuildLinkedIssueDtosAsync(db, projectId, links);
        return linked.Count(i => !EpicProgress.IsCompleted(i));
    }

    private static async Task<List<LinkedIssueDto>> BuildLinkedIssueDtosAsync(MohistDbContext db, string projectId, IReadOnlyList<EpicIssueRow> links)
    {
        if (links.Count == 0) return [];
        var issueNumbers = links.Select(l => l.IssueNumber).Distinct().ToArray();
        var rows = await db.IssueStates.AsNoTracking()
            .ToListAsync();
        var byNumber = IssueStateReader.SelectCanonicalByNumber(rows, projectId, issueNumbers);

        return links
            .OrderBy(l => l.CreatedAt)
            .Select(link => byNumber.TryGetValue(link.IssueNumber, out var issue)
                ? new LinkedIssueDto(
                    Id: issue.Id,
                    Number: issue.Number,
                    Title: issue.Title,
                    Status: MohistDefaultWorkflowProjection.IssueStatusName(issue.Status),
                    Stage: "",
                    Health: MohistDefaultWorkflowProjection.Health(issue.Status),
                    Priority: issue.Priority)
                : null)
            .Where(dto => dto is not null)
            .Cast<LinkedIssueDto>()
            .ToList();
    }

    public async Task<EpicDto?> UpdateAsync(string? title, string? description, string? priority)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];
        var projectId = parts[0];

        var epic = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (epic is null) return null;

        if (title is not null) epic.Title = title;
        if (description is not null) epic.Description = description;
        if (priority is not null) epic.Priority = string.IsNullOrWhiteSpace(priority) ? "p2" : priority;
        epic.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return ToDto(epic);
    }

    private static EpicDto ToDto(EpicRow epic) =>
        new(epic.Id, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"));
}
