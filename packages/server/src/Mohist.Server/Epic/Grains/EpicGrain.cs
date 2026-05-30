using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epics;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Epic.Grains;

public class EpicGrain : Grain, IEpicGrain
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public EpicGrain(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public async Task<EpicDto> CreateAsync(string projectId, string title, string? description, string? priority)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var epic = new EpicEntry
        {
            Id = $"epic_{Guid.NewGuid():N}",
            ProjectId = projectId,
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
            db.EpicIssues.Add(new EpicIssueEntry
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
        epic.Status = status;
        epic.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return ToDto(epic);
    }

    private static EpicDto ToDto(EpicEntry epic) =>
        new(epic.Id, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"));
}
