using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using EpicAggregate = Mohist.Server.Epic.Domain.Epic;
using EpicStatusEnum = Mohist.Server.Epic.Domain.EpicStatus;

namespace Mohist.Server.Epic.Grains;

public class EpicGrain : Grain, IEpicGrain
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;
    private readonly ILogger<EpicGrain> _log;

    public EpicGrain(
        IDbContextFactory<MohistDbContext> dbFactory,
        IGrainFactory grains,
        ILogger<EpicGrain> log)
    {
        _dbFactory = dbFactory;
        _grains = grains;
        _log = log;
    }

    internal string GrainKeyForTest { get; set; } = string.Empty;

    private string GrainKey => string.IsNullOrEmpty(GrainKeyForTest) ? this.GetPrimaryKeyString() : GrainKeyForTest;

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

    public async Task<EpicDto> PauseAsync(string? reason)
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
        // Idempotency at the boundary is owned by the domain's status
        // checks (e.g. Pause-from-Paused short-circuits without throwing).
        // Invalid non-target transitions (Pause-from-Idle raises
        // EpicPauseRequiresRunningException; Pause-from-Terminal raises
        // EpicAlreadyTerminalException) propagate so the HTTP layer can
        // surface them as 409 EPIC_NOT_RUNNING / 409 EPIC_ALREADY_TERMINAL.
        var now = DateTimeOffset.UtcNow;
        domain.Pause(reason, now.UtcDateTime);
        MapToRow(domain, row, now);
        ApplyPendingEvents(db, domain, projectId, epicId);
        await db.SaveChangesAsync();
        domain.ClearPendingEvents();
        return ToDto(row);
    }

    public async Task<EpicDto> StartAsync()
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
        // Idempotency at the boundary is owned by the domain's status
        // checks (Start-from-Running short-circuits without throwing).
        // Invalid non-target transitions (Start-from-Paused raises
        // EpicStartRequiresIdleException; Start-from-Terminal raises
        // EpicAlreadyTerminalException) propagate so the HTTP layer can
        // surface them as 409 EPIC_START_REQUIRES_IDLE / 409 EPIC_ALREADY_TERMINAL.
        var now = DateTimeOffset.UtcNow;
        var wasAlreadyRunning = row.Status == EpicStatusName.Running;
        domain.Start(now.UtcDateTime);
        MapToRow(domain, row, now);
        ApplyPendingEvents(db, domain, projectId, epicId);
        await db.SaveChangesAsync();
        domain.ClearPendingEvents();

        if (row.Status == EpicStatusName.Running && !wasAlreadyRunning)
        {
            return await TryStartNextAsync(db, projectId, epicId, row, links);
        }
        return ToDto(row);
    }

    public async Task<EpicDto> ResumeAsync()
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
        // Idempotency at the boundary is owned by the domain's status
        // checks (Resume-from-Running short-circuits without throwing).
        // Invalid non-target transitions (Resume-from-Idle raises
        // EpicResumeRequiresPausedException; Resume-from-Terminal raises
        // EpicAlreadyTerminalException) propagate so the HTTP layer can
        // surface them as 409 EPIC_RESUME_REQUIRES_PAUSED / 409 EPIC_ALREADY_TERMINAL.
        var now = DateTimeOffset.UtcNow;
        var wasAlreadyRunning = row.Status == EpicStatusName.Running;
        domain.Resume(now.UtcDateTime);
        MapToRow(domain, row, now);
        ApplyPendingEvents(db, domain, projectId, epicId);
        await db.SaveChangesAsync();
        domain.ClearPendingEvents();

        if (row.Status == EpicStatusName.Running && !wasAlreadyRunning)
        {
            return await ReconcileAfterTerminalInternalAsync(db, projectId, epicId, row, links);
        }
        return ToDto(row);
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

    public async Task<EpicDto?> AutoMarkDoneIfReadyAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];
        var projectId = parts[0];

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (row is null) return null;

        return await TryAutoMarkDoneAsync(db, projectId, epicId, row);
    }

    public async Task<EpicDto?> ReconcileAfterTerminalAsync()
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

        return await ReconcileAfterTerminalInternalAsync(db, projectId, epicId, row, links);
    }

    /// <summary>
    /// Core reconcile-on-terminal-event logic, shared by the
    /// <see cref="ReconcileAfterTerminalAsync"/> grain entry point and
    /// <see cref="ResumeAsync"/>'s post-resume re-evaluation.
    ///
    /// Behavior:
    /// <list type="bullet">
    /// <item>Skips terminal (done/closed) and paused epics — no advance.</item>
    /// <item>Marks done when readiness is satisfied.</item>
    /// <item>For a <c>running</c> epic, calls <see cref="TryStartNextAsync"/>;
    /// an <c>idle</c> epic does not auto-advance.</item>
    /// </list>
    /// </summary>
    private async Task<EpicDto> ReconcileAfterTerminalInternalAsync(
        MohistDbContext db, string projectId, string epicId, EpicRow row, IReadOnlyList<EpicIssueRow> links)
    {
        if (row.Status is EpicStatusName.Done or EpicStatusName.Closed)
            return ToDto(row);
        if (row.Status == EpicStatusName.Paused)
            return ToDto(row);

        var undelivered = await ComputeUndeliveredLinkedNumbersAsync(db, projectId, links);
        if (undelivered.Count == 0)
        {
            var domain = Materialize(row, links);
            var now = DateTimeOffset.UtcNow;
            domain.MarkDone(undelivered, now.UtcDateTime);
            MapToRow(domain, row, now);
            ApplyPendingEvents(db, domain, projectId, epicId);
            await db.SaveChangesAsync();
            domain.ClearPendingEvents();
            return ToDto(row);
        }

        if (row.Status == EpicStatusName.Running)
        {
            return await TryStartNextAsync(db, projectId, epicId, row, links);
        }

        // idle: not self-driving; do not advance.
        return ToDto(row);
    }

    /// <summary>
    /// Advance the next startable linked issue for a <c>running</c> epic.
    /// Idempotent and safe to call repeatedly: returns without starting
    /// when the serial in-progress slot is occupied, when nothing is
    /// startable, or when the previous start attempt already left the
    /// epic in a stable state. Exceptions from
    /// <see cref="IIssueGrain.StartWorkAsync"/> are caught and logged —
    /// the epic remains <c>running</c> (running-but-idle) so the next
    /// reconcile retry can re-evaluate. The serial "at most one
    /// in-progress" rule is expressed here as a runtime check
    /// (capacity N=1), leaving room for future multi-runner parallelism.
    /// </summary>
    private async Task<EpicDto> TryStartNextAsync(
        MohistDbContext db, string projectId, string epicId, EpicRow row, IReadOnlyList<EpicIssueRow> links)
    {
        if (row.Status != EpicStatusName.Running)
            return ToDto(row);

        var linked = await BuildLinkedIssueDtosAsync(db, projectId, links);
        if (linked.Any(i => i.Status == "in_progress"))
            return ToDto(row);

        var undelivered = linked
            .Where(i => !EpicProgress.IsCompleted(i) && i.Status != "cancelled")
            .ToList();

        var next = EpicProgress.SelectStartableNext(undelivered);
        if (next is null)
        {
            // Running-but-idle: nothing currently startable. The read
            // model (EpicQuerier → EpicProgress.Build) computes the
            // nextIssueReason for the dashboard; no further state
            // mutation is required here.
            return ToDto(row);
        }

        try
        {
            var issueGrain = _grains.GetGrain<IIssueGrain>(Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(next.Id));
            await issueGrain.StartWorkAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Epic {EpicId} ({ProjectId}) failed to start next linked issue {IssueId}; epic remains running-but-idle",
                epicId, projectId, next.Id);
        }

        return ToDto(row);
    }

    private async Task<EpicDto> TryAutoMarkDoneAsync(MohistDbContext db, string projectId, string epicId, EpicRow row)
    {
        if (row.Status is "done" or "closed")
            return ToDto(row);
        if (row.Status == "paused")
            return ToDto(row);

        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .ToListAsync();
        var undelivered = await ComputeUndeliveredLinkedNumbersAsync(db, projectId, links);
        if (undelivered.Count > 0)
            return ToDto(row);

        var domain = Materialize(row, links);
        var now = DateTimeOffset.UtcNow;
        domain.MarkDone(undelivered, now.UtcDateTime);
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

        // Build the set of undelivered issue numbers across the linked
        // issues so each issue's StartBlocker can be evaluated against
        // peer state. Cancelled issues are NOT considered delivered
        // (they are excluded from selection by EpicProgress anyway, and
        // other in-flight work should still be respected as prereqs).
        var undeliveredPrereqNumbers = new HashSet<int>(
            byNumber.Values
                .Where(i => i.Status is not (IssueStatus.Done or IssueStatus.Cancelled))
                .Select(i => i.Number));

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
                    Priority: issue.Priority,
                    CanStart: issue.CanStart(undeliveredPrereqNumbers),
                    StartBlocker: IssueStartBlockerDto.FromDomain(issue.StartBlocker(undeliveredPrereqNumbers)))
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
            PauseReason = row.PauseReason,
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
        PauseReason = epic.PauseReason,
        CreatedAt = epic.CreatedAt == default ? now : new DateTimeOffset(epic.CreatedAt, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(epic.UpdatedAt, TimeSpan.Zero),
    };

    private static void MapToRow(EpicAggregate epic, EpicRow row, DateTimeOffset now)
    {
        row.Title = epic.Title;
        row.Description = epic.Description;
        row.Priority = epic.Priority;
        row.Status = StatusName(epic.Status);
        row.PauseReason = epic.PauseReason;
        row.UpdatedAt = new DateTimeOffset(epic.UpdatedAt, TimeSpan.Zero);
        if (row.CreatedAt == default) row.CreatedAt = now;
    }

    private static string StatusName(EpicStatusEnum status) => EpicStatusName.ToName(status);

    private static EpicStatusEnum ParseStatus(string status) => EpicStatusName.Parse(status);

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
        new(epic.Id, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), epic.PauseReason);
}