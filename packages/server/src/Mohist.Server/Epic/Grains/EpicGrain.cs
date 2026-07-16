using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
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
    private readonly TimeProvider _timeProvider;
    private readonly IEventStore _eventStore;
    private readonly ILogger<EpicGrain> _log;

    public EpicGrain(
        IDbContextFactory<MohistDbContext> dbFactory,
        IGrainFactory grains,
        TimeProvider timeProvider,
        IEventStore eventStore,
        ILogger<EpicGrain> log)
    {
        _dbFactory = dbFactory;
        _grains = grains;
        _timeProvider = timeProvider;
        _eventStore = eventStore;
        _log = log;
    }

    internal string GrainKeyForTest { get; set; } = string.Empty;

    private string GrainKey => string.IsNullOrEmpty(GrainKeyForTest) ? this.GetPrimaryKeyString() : GrainKeyForTest;

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();

    public async Task<EpicDto> CreateAsync(string projectId, int number, string title, string? description, string? priority)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var now = Now();
        var epic = EpicAggregate.Create(
            projectId: projectId,
            number: number,
            title: title,
            description: description,
            priority: priority,
            now: now.UtcDateTime);
        var row = MapToRow(epic, now);
        db.Epics.Add(row);
        var pending = DrainPendingEvents(epic);
        await PersistEpicEventsAsync(db, epic, pending, now);
        await db.SaveChangesAsync();
        return ToDto(row);
    }

    public async Task LinkIssueAsync(int issueNumber, string projectId)
    {
        var (_, epicNumber) = ParseGrainKey();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var epic = await db.Epics.AsNoTracking()
            .FirstOrDefaultAsync(row => row.ProjectId == projectId && row.Number == epicNumber);
        if (epic is null) throw new InvalidOperationException($"Epic #{epicNumber} not found");

        var issue = await db.Issues.AsNoTracking()
            .FirstOrDefaultAsync(row => row.ProjectId == projectId && row.Number == issueNumber);
        if (issue?.EpicNumber == epicNumber) return;
        if (epic.Status == EpicStatusName.Closed)
            throw new EpicClosedCannotLinkException(epicNumber);

        var target = _grains.GetGrain<IIssueGrain>(
            Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await target.AssignEpicAsync(epicNumber);
    }

    public async Task<IReadOnlyList<BatchMembershipOutcome>> LinkIssuesAsync(
        IReadOnlyList<BatchMembershipRequestItem> issues,
        string projectId)
    {
        if (issues.Count == 0) return [];
        var (_, epicNumber) = ParseGrainKey();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var epic = await db.Epics.AsNoTracking()
            .FirstOrDefaultAsync(row => row.ProjectId == projectId && row.Number == epicNumber);
        if (epic is null) throw new InvalidOperationException($"Epic #{epicNumber} not found");

        var results = new List<BatchMembershipOutcome>();
        foreach (var item in issues.Where(item => item.IssueNumber > 0).DistinctBy(item => item.IssueNumber))
        {
            var issue = await db.Issues.AsNoTracking()
                .FirstOrDefaultAsync(row => row.ProjectId == projectId && row.Number == item.IssueNumber);
            if (issue is null)
            {
                results.Add(BatchMembershipOutcome.NotFound(item.Identifier));
                continue;
            }
            if (issue.EpicNumber == epicNumber)
            {
                results.Add(BatchMembershipOutcome.AlreadyLinked(item.Identifier, item.IssueNumber));
                continue;
            }
            if (epic.Status == EpicStatusName.Closed)
                throw new EpicClosedCannotLinkException(epicNumber);

            var target = _grains.GetGrain<IIssueGrain>(
                Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(new IssueKey(projectId, item.IssueNumber)));
            await target.AssignEpicAsync(epicNumber);
            results.Add(BatchMembershipOutcome.Linked(item.Identifier, item.IssueNumber));
        }
        return results;
    }

    public async Task UnlinkIssueAsync(int issueNumber, string projectId)
    {
        var (_, epicNumber) = ParseGrainKey();
        var target = _grains.GetGrain<IIssueGrain>(
            Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await target.RemoveEpicAsync(epicNumber);
    }

    public async Task<IReadOnlyList<BatchMembershipOutcome>> UnlinkIssuesAsync(
        IReadOnlyList<BatchMembershipRequestItem> issues,
        string projectId)
    {
        if (issues.Count == 0) return [];
        var (_, epicNumber) = ParseGrainKey();
        var results = new List<BatchMembershipOutcome>();
        foreach (var item in issues.Where(item => item.IssueNumber > 0).DistinctBy(item => item.IssueNumber))
        {
            var target = _grains.GetGrain<IIssueGrain>(
                Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(new IssueKey(projectId, item.IssueNumber)));
            var removed = await target.RemoveEpicAsync(epicNumber);
            results.Add(removed
                ? BatchMembershipOutcome.Unlinked(item.Identifier, item.IssueNumber)
                : BatchMembershipOutcome.WasNotAMember(item.Identifier, item.IssueNumber));
        }
        return results;
    }


    public async Task<EpicDto> PauseAsync(string? reason)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var (projectId, epicNumber) = ParseGrainKey();

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == epicNumber);
        if (row is null) throw new InvalidOperationException($"Epic #{epicNumber} not found");

        var links = await LoadLinkedIssueNumbersAsync(db, projectId, epicNumber);
        var domain = Materialize(row);
        // Idempotency at the boundary is owned by the domain's status
        // checks (e.g. Pause-from-Paused short-circuits without throwing).
        // Invalid non-target transitions (Pause-from-Idle raises
        // EpicPauseRequiresRunningException; Pause-from-Terminal raises
        // EpicAlreadyTerminalException) propagate so the HTTP layer can
        // surface them as 409 EPIC_NOT_RUNNING / 409 EPIC_ALREADY_TERMINAL.
        var now = Now();
        domain.Pause(reason, now.UtcDateTime);
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();
        return ToDto(row);
    }

    public async Task<EpicDto> StartAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var (projectId, epicNumber) = ParseGrainKey();

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == epicNumber);
        if (row is null) throw new InvalidOperationException($"Epic #{epicNumber} not found");

        var links = await LoadLinkedIssueNumbersAsync(db, projectId, epicNumber);
        var domain = Materialize(row);
        // Idempotency at the boundary is owned by the domain's status
        // checks (Start-from-Running short-circuits without throwing).
        // Invalid non-target transitions (Start-from-Paused raises
        // EpicStartRequiresIdleException; Start-from-Terminal raises
        // EpicAlreadyTerminalException) propagate so the HTTP layer can
        // surface them as 409 EPIC_START_REQUIRES_IDLE / 409 EPIC_ALREADY_TERMINAL.
        var now = Now();
        var wasAlreadyRunning = row.Status == EpicStatusName.Running;
        domain.Start(now.UtcDateTime);
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        // Persist the status-changed event atomically with the state transition
        // so the EpicRunningStatusHandler durable trigger survives or rolls back
        // together. The handler re-drives recompute if the command-path
        // TryStartNextAsync never runs (crash after commit).
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();

        if (row.Status == EpicStatusName.Running && !wasAlreadyRunning)
        {
            return await TryStartNextAsync(db, projectId, epicNumber, row, links);
        }
        return ToDto(row);
    }

    public async Task<EpicDto> ResumeAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var (projectId, epicNumber) = ParseGrainKey();

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == epicNumber);
        if (row is null) throw new InvalidOperationException($"Epic #{epicNumber} not found");

        var links = await LoadLinkedIssueNumbersAsync(db, projectId, epicNumber);
        var domain = Materialize(row);
        // Idempotency at the boundary is owned by the domain's status
        // checks (Resume-from-Running short-circuits without throwing).
        // Invalid non-target transitions (Resume-from-Idle raises
        // EpicResumeRequiresPausedException; Resume-from-Terminal raises
        // EpicAlreadyTerminalException) propagate so the HTTP layer can
        // surface them as 409 EPIC_RESUME_REQUIRES_PAUSED / 409 EPIC_ALREADY_TERMINAL.
        var now = Now();
        var wasAlreadyRunning = row.Status == EpicStatusName.Running;
        domain.Resume(now.UtcDateTime);
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();

        if (row.Status == EpicStatusName.Running && !wasAlreadyRunning)
        {
            return await RecomputeProgressInternalAsync(db, projectId, epicNumber, row, links);
        }
        return ToDto(row);
    }

    public async Task<EpicDto> SetStatusAsync(string status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var (projectId, epicNumber) = ParseGrainKey();

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == epicNumber);
        if (row is null) throw new InvalidOperationException($"Epic #{epicNumber} not found");

        var links = await LoadLinkedIssueNumbersAsync(db, projectId, epicNumber);
        var domain = Materialize(row);
        var now = Now();

        switch (status?.ToLowerInvariant())
        {
            case "done":
            {
                var open = await ComputeOpenLinkedNumbersAsync(db, projectId, links);
                domain.MarkDone(open, now.UtcDateTime);
                break;
            }
            case "closed":
                domain.Close(now.UtcDateTime);
                break;
            default:
                throw new InvalidOperationException($"Unknown epic status '{status}'");
        }

        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();
        return ToDto(row);
    }

    public async Task<EpicDto> ReopenAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var (projectId, epicNumber) = ParseGrainKey();

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == epicNumber);
        if (row is null) throw new InvalidOperationException($"Epic #{epicNumber} not found");

        var links = await LoadLinkedIssueNumbersAsync(db, projectId, epicNumber);
        var domain = Materialize(row);
        // Reopen only accepts terminal epics; non-terminal attempts raise
        // EpicNotTerminalException so the HTTP layer can return 409
        // EPIC_NOT_TERMINAL. EnsureNotTerminal remains in place for the
        // other transitions (Start/Pause/Resume/Done/Close) — Reopen is
        // the single, explicit exit from a terminal state.
        var now = Now();
        domain.Reopen(now.UtcDateTime);
        MapToRow(domain, row, now);

        var pending = DrainPendingEvents(domain);
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();
        return ToDto(row);
    }

    public async Task<EpicDto?> UpdateAsync(string? title, string? description, string? priority)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var (projectId, epicNumber) = ParseGrainKey();

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == epicNumber);
        if (row is null) return null;

        var links = await LoadLinkedIssueNumbersAsync(db, projectId, epicNumber);
        var domain = Materialize(row);
        var now = Now();
        domain.Update(title, description, priority, now.UtcDateTime);
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();
        return ToDto(row);
    }

    public async Task<EpicDto?> AutoMarkDoneIfReadyAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var (projectId, epicNumber) = ParseGrainKey();

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == epicNumber);
        if (row is null) return null;

        return await TryAutoMarkDoneAsync(db, projectId, epicNumber, row);
    }

    public async Task<EpicDto?> RecomputeProgressAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var (projectId, epicNumber) = ParseGrainKey();

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Number == epicNumber);
        if (row is null) return null;

        var links = await LoadLinkedIssueNumbersAsync(db, projectId, epicNumber);

        return await RecomputeProgressInternalAsync(db, projectId, epicNumber, row, links, StartFailureMode.Propagate);
    }

    /// <summary>
    /// Core recompute-progress logic, shared by the
    /// <see cref="RecomputeProgressAsync"/> grain entry point,
    /// <see cref="ResumeAsync"/>'s post-resume re-evaluation, and the
    /// link-time trigger added in <see cref="LinkIssueAsync"/> /
    /// <see cref="LinkIssuesAsync"/>.
    ///
    /// Behavior:
    /// <list type="bullet">
    /// <item>Skips terminal (done/closed) and paused epics — no advance.</item>
    /// <item>Marks done when readiness is satisfied.</item>
    /// <item>For a <c>running</c> epic, calls <see cref="TryStartNextAsync"/>;
    /// an <c>idle</c> epic does not auto-advance.</item>
    /// </list>
    /// </summary>
    /// <param name="startFailureMode">
    /// <c>Propagate</c> for terminal-event recompute — failures escape
    /// to the durable dispatcher for retry/dead-lettering. <c>PreserveRunning</c>
    /// for command paths (Resume, link) — failures keep the epic
    /// running-but-idle so the next event-driven recompute can re-evaluate.
    /// </param>
    private async Task<EpicDto> RecomputeProgressInternalAsync(
        MohistDbContext db, string projectId, int epicNumber, EpicRow row, IReadOnlyList<int> links,
        StartFailureMode startFailureMode = StartFailureMode.PreserveRunning)
    {
        if (row.Status == EpicStatusName.Closed)
            return ToDto(row);
        if (row.Status == EpicStatusName.Paused)
            return ToDto(row);

        var open = await ComputeOpenLinkedNumbersAsync(db, projectId, links);
        if (row.Status == EpicStatusName.Done)
        {
            if (open.Count == 0) return ToDto(row);
            var domain = Materialize(row);
            var now = Now();
            domain.WakeFromDone(now.UtcDateTime);
            MapToRow(domain, row, now);
            var pending = DrainPendingEvents(domain);
            await PersistEpicEventsAsync(db, domain, pending, now);
            await db.SaveChangesAsync();
            return await TryStartNextAsync(db, projectId, epicNumber, row, links, startFailureMode);
        }
        if (open.Count == 0)
        {
            var domain = Materialize(row);
            var now = Now();
            domain.MarkDone(open, now.UtcDateTime);
            MapToRow(domain, row, now);
            var pending = DrainPendingEvents(domain);
            await PersistEpicEventsAsync(db, domain, pending, now);
            await db.SaveChangesAsync();
            return ToDto(row);
        }

        if (row.Status == EpicStatusName.Running)
        {
            return await TryStartNextAsync(db, projectId, epicNumber, row, links, startFailureMode);
        }

        // idle: not self-driving; do not advance.
        return ToDto(row);
    }

    /// <summary>
    /// Advance the next startable linked issue for a <c>running</c> epic.
    /// Idempotent and safe to call repeatedly: returns without starting
    /// when the serial in-progress slot is occupied, when nothing is
    /// startable, or when the previous start attempt already left the
    /// epic in a stable state. The serial "at most one
    /// in-progress" rule is expressed here as a runtime check
    /// (capacity N=1), leaving room for future multi-runner parallelism.
    ///
    /// Start-failure contract:
    /// <list type="bullet">
    /// <item><c>PreserveRunning</c>: <see cref="IIssueGrain.StartWorkAsync"/>
    /// failures are caught and logged — the epic remains <c>running</c>
    /// (running-but-idle) so the next event-driven recompute can
    /// re-evaluate. Used by <see cref="StartAsync"/>,
    /// <see cref="ResumeAsync"/>, and link operations.</item>
    /// <item><c>Propagate</c>: failures escape untouched so the durable
    /// dispatcher retries / dead-letters the terminal-event handler
    /// delivery. Used by <see cref="RecomputeProgressAsync"/>.</item>
    /// </list>
    /// </summary>
    private async Task<EpicDto> TryStartNextAsync(
        MohistDbContext db, string projectId, int epicNumber, EpicRow row, IReadOnlyList<int> links,
        StartFailureMode startFailureMode = StartFailureMode.PreserveRunning)
    {
        if (row.Status != EpicStatusName.Running)
            return ToDto(row);

        var linked = await BuildLinkedIssueDtosAsync(db, projectId, links);
        if (linked.Any(i => i.Status == "in_progress"))
            return ToDto(row);

        var open = linked
            .Where(EpicProgress.IsOpen)
            .ToList();

        var next = EpicProgress.SelectStartableNext(open);
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
            var issueGrain = _grains.GetGrain<IIssueGrain>(
                Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(new IssueKey(projectId, next.Number)));
            await issueGrain.TryStartFromEpicAsync(epicNumber);
        }
        catch (Exception ex)
        {
            if (startFailureMode == StartFailureMode.PreserveRunning)
            {
                _log.LogWarning(ex,
                    "Epic #{EpicNumber} ({ProjectId}) failed to start next linked issue #{IssueNumber}; epic remains running-but-idle, emitting start-attempt-failed for durable retry",
                    epicNumber, projectId, next.Number);
                var now = _timeProvider.GetUtcNow();
                var domain = Materialize(row);
                domain.RecordStartAttemptFailure(next.Number, "start-failed", now.UtcDateTime);
                MapToRow(domain, row, now);
                var pending = DrainPendingEvents(domain);
                await PersistEpicEventsAsync(db, domain, pending, now);
                await db.SaveChangesAsync();
                return ToDto(row);
            }
            throw;
        }

        return ToDto(row);
    }

    private enum StartFailureMode
    {
        PreserveRunning,
        Propagate,
    }

    private async Task<EpicDto> TryAutoMarkDoneAsync(MohistDbContext db, string projectId, int epicNumber, EpicRow row)
    {
        if (row.Status is "done" or "closed")
            return ToDto(row);
        if (row.Status == "paused")
            return ToDto(row);

        var links = await LoadLinkedIssueNumbersAsync(db, projectId, epicNumber);
        var open = await ComputeOpenLinkedNumbersAsync(db, projectId, links);
        if (open.Count > 0)
            return ToDto(row);

        var domain = Materialize(row);
        var now = Now();
        domain.MarkDone(open, now.UtcDateTime);
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();
        return ToDto(row);
    }

    private async Task<HashSet<int>> ComputeOpenLinkedNumbersAsync(
        MohistDbContext db, string projectId, IReadOnlyList<int> links)
    {
        if (links.Count == 0) return new HashSet<int>();
        var linked = await BuildLinkedIssueDtosAsync(db, projectId, links);
        var open = new HashSet<int>();
        foreach (var dto in linked)
        {
            if (EpicProgress.IsOpen(dto))
                open.Add(dto.Number);
        }
        return open;
    }

    private static async Task<List<LinkedIssueDto>> BuildLinkedIssueDtosAsync(MohistDbContext db, string projectId, IReadOnlyList<int> links)
    {
        if (links.Count == 0) return [];
        var issueNumbers = links.Distinct().ToArray();
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number != null && issueNumbers.Contains(row.Number.Value))
            .ToListAsync();
        var byNumber = IssueRowMapper.ByNumber(rows, projectId, issueNumbers);

        // Build the set of undelivered issue numbers across the linked
        // issues so each issue's StartBlocker can be evaluated against
        // peer state. Only a done prerequisite is delivered; cancellation
        // remains a blocker for a dependent issue.
        var undeliveredPrereqNumbers = new HashSet<int>(
            byNumber.Values
                .Where(i => i.Status != IssueStatus.Done)
                .Select(i => i.Number));

        // Also fetch external prerequisites (prerequisite numbers that are
        // NOT epic members) so StartBlocker can detect them. Without this,
        // a member blocked by an external prerequisite appears startable,
        // gets selected by TryStartNext, and StartWorkAsync rejects it —
        // producing spurious EpicStartAttemptFailed noise.
        var allPrereqNumbers = byNumber.Values
            .SelectMany(i => i.PrerequisiteNumbers)
            .Distinct()
            .Where(n => !undeliveredPrereqNumbers.Contains(n))
            .ToArray();
        if (allPrereqNumbers.Length > 0)
        {
            var prereqIssues = IssueRowMapper.ByNumber(
                await db.Issues.AsNoTracking()
                    .Where(row => row.ProjectId == projectId && row.Number != null && allPrereqNumbers.Contains(row.Number.Value))
                    .ToListAsync(),
                projectId,
                allPrereqNumbers);
            foreach (var prereqIssue in prereqIssues.Values)
            {
                if (prereqIssue.Status != IssueStatus.Done)
                    undeliveredPrereqNumbers.Add(prereqIssue.Number);
            }
        }

        return issueNumbers
            .OrderBy(number => number)
            .Select(number => byNumber.TryGetValue(number, out var issue)
                ? new LinkedIssueDto(
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

    private static Task<List<int>> LoadLinkedIssueNumbersAsync(
        MohistDbContext db,
        string projectId,
        int epicNumber) =>
        db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.EpicNumber == epicNumber && row.Number != null)
            .OrderBy(row => row.Number)
            .Select(row => row.Number!.Value)
            .ToListAsync();

    private static EpicAggregate Materialize(EpicRow row)
    {
        var epic = new EpicAggregate
        {
            ProjectId = row.ProjectId,
            Number = row.Number,
            Title = row.Title,
            Description = row.Description,
            Priority = row.Priority,
            Status = ParseStatus(row.Status),
            PauseReason = row.PauseReason,
            CreatedAt = row.CreatedAt.UtcDateTime,
            UpdatedAt = row.UpdatedAt.UtcDateTime,
        };
        return epic;
    }

    private static EpicRow MapToRow(EpicAggregate epic, DateTimeOffset now) => new()
    {
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

    private (string ProjectId, int EpicNumber) ParseGrainKey()
    {
        ScopedGrainKeyCodec.Parse(GrainKey, out var projectId, out var epicNumber);
        return (projectId, epicNumber);
    }

    private static IReadOnlyList<Epic.Domain.Events.EpicEvent> DrainPendingEvents(EpicAggregate epic)
    {
        var pending = epic.PendingEvents.ToList();
        epic.ClearPendingEvents();
        return pending;
    }

    /// <summary>
    /// Stages every Epic domain event in the aggregate's database context so
    /// state and events commit or roll back together in one SaveChanges call.
    /// </summary>
    private async Task PersistEpicEventsAsync(
        MohistDbContext db,
        EpicAggregate epic,
        IReadOnlyList<Epic.Domain.Events.EpicEvent> events,
        DateTimeOffset now)
    {
        if (events.Count == 0) return;
        var source = EpicEventPersistence.EpicSource(epic.ProjectId, epic.Number);
        var subject = epic.Number.ToString();
        var extensions = EpicLineage.BuildExtensions(epic);

        foreach (var evt in events)
        {
            var type = EpicEventSerializer.BusType(evt);
            var dataJson = EpicEventSerializer.ToData(evt);
            var envelope = new CloudEvent(
                id: Guid.NewGuid().ToString(),
                source: new Uri(source, UriKind.Relative),
                type: type,
                time: now,
                data: dataJson,
                subject: subject,
                extensions: extensions);

            await _eventStore.AppendAsync(db, envelope, CancellationToken.None);
        }
    }

    private static EpicDto ToDto(EpicRow epic) =>
        new(epic.ProjectId, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), epic.PauseReason);

}
