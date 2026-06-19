using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using EpicAggregate = Mohist.Server.Epic.Domain.Epic;
using EpicStatusEnum = Mohist.Server.Epic.Domain.EpicStatus;

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
        var epic = EpicAggregate.Create(
            id: $"epic_{Guid.NewGuid():N}",
            projectId: projectId,
            number: number,
            title: title,
            description: description,
            priority: priority);
        var row = MapToRow(epic, now);
        db.Epics.Add(row);
        await db.SaveChangesAsync();
        epic.ClearPendingEvents();
        return ToDto(row);
    }

    public async Task LinkIssueAsync(string issueId, int issueNumber, string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (row is null) throw new InvalidOperationException($"Epic {epicId} not found");

        var existing = await db.EpicIssues.AsNoTracking()
            .FirstOrDefaultAsync(link => link.ProjectId == projectId && link.IssueId == issueId);
        if (existing is not null && existing.EpicId != epicId)
        {
            var existingEpic = await db.Epics.AsNoTracking()
                .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == existing.EpicId);
            throw new InvalidOperationException($"Issue already belongs to Epic '{existing.EpicId}'{(existingEpic is not null ? $" ({existingEpic.Title})" : "")}");
        }

        if (existing is not null) return;

        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .ToListAsync();
        var domain = Materialize(row, links);
        var now = DateTimeOffset.UtcNow;
        domain.LinkIssue(issueId, issueNumber, now.UtcDateTime);

        db.EpicIssues.Add(new EpicIssueRow
        {
            EpicId = epicId,
            ProjectId = projectId,
            IssueId = issueId,
            IssueNumber = issueNumber,
        });
        MapToRow(domain, row, now);
        ApplyPendingEvents(db, domain, projectId, epicId);
        await db.SaveChangesAsync();
        domain.ClearPendingEvents();
    }

    public async Task UnlinkIssueAsync(string issueId, string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (row is null) return;

        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .ToListAsync();
        var domain = Materialize(row, links);
        var now = DateTimeOffset.UtcNow;
        domain.UnlinkIssue(issueId, now.UtcDateTime);

        var link = await db.EpicIssues.FirstOrDefaultAsync(
            l => l.ProjectId == projectId && l.EpicId == epicId && l.IssueId == issueId);
        if (link is not null) db.EpicIssues.Remove(link);
        MapToRow(domain, row, now);
        ApplyPendingEvents(db, domain, projectId, epicId);
        await db.SaveChangesAsync();
        domain.ClearPendingEvents();
    }

    public async Task<EpicDto> SetStatusAsync(string status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];
        var projectId = parts[0];

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (row is null) throw new InvalidOperationException($"Epic {epicId} not found");

        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .ToListAsync();
        var domain = Materialize(row, links);
        var now = DateTimeOffset.UtcNow;

        switch (status?.ToLowerInvariant())
        {
            case "done":
            {
                var undelivered = await ComputeUndeliveredLinkedNumbersAsync(db, projectId, links);
                domain.MarkDone(undelivered, now.UtcDateTime);
                break;
            }
            case "closed":
                domain.Close(now.UtcDateTime);
                break;
            default:
                throw new InvalidOperationException($"Unknown epic status '{status}'");
        }

        MapToRow(domain, row, now);
        ApplyPendingEvents(db, domain, projectId, epicId);
        await db.SaveChangesAsync();
        domain.ClearPendingEvents();
        return ToDto(row);
    }

    public async Task<EpicDto?> UpdateAsync(string? title, string? description, string? priority)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];
        var projectId = parts[0];

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (row is null) return null;

        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .ToListAsync();
        var domain = Materialize(row, links);
        var now = DateTimeOffset.UtcNow;
        domain.Update(title, description, priority, now.UtcDateTime);
        MapToRow(domain, row, now);
        ApplyPendingEvents(db, domain, projectId, epicId);
        await db.SaveChangesAsync();
        domain.ClearPendingEvents();
        return ToDto(row);
    }

    private async Task<HashSet<int>> ComputeUndeliveredLinkedNumbersAsync(
        MohistDbContext db, string projectId, IReadOnlyList<EpicIssueRow> links)
    {
        if (links.Count == 0) return new HashSet<int>();
        var linked = await BuildLinkedIssueDtosAsync(db, projectId, links);
        var undelivered = new HashSet<int>();
        foreach (var dto in linked)
        {
            if (!EpicProgress.IsCompleted(dto))
                undelivered.Add(dto.Number);
        }
        return undelivered;
    }

    private static async Task<List<LinkedIssueDto>> BuildLinkedIssueDtosAsync(MohistDbContext db, string projectId, IReadOnlyList<EpicIssueRow> links)
    {
        if (links.Count == 0) return [];
        var issueNumbers = links.Select(l => l.IssueNumber).Distinct().ToArray();
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number != null && issueNumbers.Contains(row.Number.Value))
            .ToListAsync();
        var byNumber = IssueRowMapper.ByNumber(rows, projectId, issueNumbers);

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

    private static EpicAggregate Materialize(EpicRow row, IReadOnlyList<EpicIssueRow> links)
    {
        var epic = new EpicAggregate
        {
            Id = row.Id,
            ProjectId = row.ProjectId,
            Number = row.Number ?? 0,
            Title = row.Title,
            Description = row.Description,
            Priority = row.Priority,
            Status = ParseStatus(row.Status),
            CreatedAt = row.CreatedAt.UtcDateTime,
            UpdatedAt = row.UpdatedAt.UtcDateTime,
        };
        foreach (var link in links)
            epic.SeedLink(link.IssueId, link.IssueNumber);
        return epic;
    }

    private static EpicRow MapToRow(EpicAggregate epic, DateTimeOffset now) => new()
    {
        Id = epic.Id,
        ProjectId = epic.ProjectId,
        Number = epic.Number,
        Title = epic.Title,
        Description = epic.Description,
        Priority = epic.Priority,
        Status = StatusName(epic.Status),
        CreatedAt = epic.CreatedAt == default ? now : new DateTimeOffset(epic.CreatedAt, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(epic.UpdatedAt, TimeSpan.Zero),
    };

    private static void MapToRow(EpicAggregate epic, EpicRow row, DateTimeOffset now)
    {
        row.Title = epic.Title;
        row.Description = epic.Description;
        row.Priority = epic.Priority;
        row.Status = StatusName(epic.Status);
        row.UpdatedAt = new DateTimeOffset(epic.UpdatedAt, TimeSpan.Zero);
        if (row.CreatedAt == default) row.CreatedAt = now;
    }

    private static string StatusName(EpicStatusEnum status) => status switch
    {
        EpicStatusEnum.Active => "active",
        EpicStatusEnum.Done => "done",
        EpicStatusEnum.Closed => "closed",
        _ => "active",
    };

    private static EpicStatusEnum ParseStatus(string status) => status?.ToLowerInvariant() switch
    {
        "done" => EpicStatusEnum.Done,
        "closed" => EpicStatusEnum.Closed,
        _ => EpicStatusEnum.Active,
    };

    private static void ApplyPendingEvents(MohistDbContext db, EpicAggregate epic, string projectId, string epicId)
    {
        var drained = epic.PendingEvents.ToArray();
        epic.ClearPendingEvents();
        foreach (var evt in drained)
        {
            if (evt is EpicClosed)
                RemoveAllLinkedIssues(db, projectId, epicId);
        }
    }

    private static void RemoveAllLinkedIssues(MohistDbContext db, string projectId, string epicId)
    {
        var links = db.EpicIssues
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .ToList();
        if (links.Count > 0)
            db.EpicIssues.RemoveRange(links);
    }

    private static EpicDto ToDto(EpicRow epic) =>
        new(epic.Id, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"));
}