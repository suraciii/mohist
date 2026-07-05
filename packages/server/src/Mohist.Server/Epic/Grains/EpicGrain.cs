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

    public async Task<EpicDto> CreateAsync(string projectId, string title, string? description, string? priority)
    {
        var number = await _grains.GetGrain<IEpicCounterGrain>(Mohist.Server.Infrastructure.Orleans.GrainKey.EpicCounter(projectId)).NextAsync();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var now = Now();
        var epic = EpicAggregate.Create(
            id: $"epic_{Guid.NewGuid():N}",
            projectId: projectId,
            number: number,
            title: title,
            description: description,
            priority: priority,
            now: now.UtcDateTime);
        var row = MapToRow(epic, now);
        db.Epics.Add(row);
        var pending = DrainPendingEvents(epic);
        await db.SaveChangesAsync();
        await PersistEpicEventsAsync(epic, pending, now);
        return ToDto(row);
    }

    public async Task LinkIssueAsync(string issueId, int issueNumber, string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (row is null) throw new InvalidOperationException($"Epic {epicId} not found");
        var targetIsTerminal = IsTerminalEpicStatus(row.Status);

        // Cross-aggregate uniqueness invariant: an issue may belong to at
        // most one non-terminal epic (idle/running/paused). Terminal target
        // epics do not consume the active slot.
        if (!targetIsTerminal && await GetActiveMembershipOwnerAsync(db, projectId, issueId, epicId) is { } conflict)
        {
            throw new InvalidOperationException(
                $"Issue already belongs to Epic '{conflict.EpicId}' ({conflict.Title})");
        }

        var alreadyLinkedToThisEpic = await db.EpicIssues.AsNoTracking()
            .AnyAsync(link => link.ProjectId == projectId && link.EpicId == epicId && link.IssueId == issueId);
        if (alreadyLinkedToThisEpic) return;

        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .ToListAsync();
        var domain = Materialize(row, links);
        var now = Now();
        domain.LinkIssue(issueId, issueNumber, now.UtcDateTime);

        db.EpicIssues.Add(new EpicIssueRow
        {
            EpicId = epicId,
            ProjectId = projectId,
            IssueId = issueId,
            IssueNumber = issueNumber,
        });
        if (!targetIsTerminal)
        {
            db.EpicActiveIssues.Add(new EpicActiveIssueRow
            {
                ProjectId = projectId,
                IssueId = issueId,
                EpicId = epicId,
                IssueNumber = issueNumber,
            });
        }
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (!targetIsTerminal)
        {
            var owner = await GetActiveMembershipOwnerAsync(projectId, issueId, epicId);
            if (owner is not null)
            {
                throw new InvalidOperationException(
                    $"Issue already belongs to Epic '{owner.EpicId}' ({owner.Title})");
            }
            throw;
        }
        await PersistEpicEventsAsync(domain, pending, now);
    }

    public async Task<IReadOnlyList<BatchMembershipOutcome>> LinkIssuesAsync(
        IReadOnlyList<BatchMembershipRequestItem> issues,
        string projectId)
    {
        if (issues.Count == 0)
            return Array.Empty<BatchMembershipOutcome>();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (row is null) throw new InvalidOperationException($"Epic {epicId} not found");
        var targetIsTerminal = IsTerminalEpicStatus(row.Status);

        // De-duplicate the input by canonical internal issue id while
        // preserving the first occurrence's caller-supplied identifier so
        // the per-identifier response matches the request one-to-one.
        // Per the spec, a duplicate identifier is "linked at most once,
        // not treated as an error" — hence the dedup key is the internal
        // id, not the identifier string.
        var dedupByIssueId = new Dictionary<string, BatchMembershipRequestItem>(StringComparer.Ordinal);
        foreach (var item in issues)
        {
            if (string.IsNullOrWhiteSpace(item.IssueId)) continue;
            dedupByIssueId.TryAdd(item.IssueId, item);
        }
        if (dedupByIssueId.Count == 0)
            return Array.Empty<BatchMembershipOutcome>();

        // Snapshot the existing link set ONCE — every successful link
        // mutates the in-memory aggregate only, persisting per-issue (so a
        // single failure does not roll back later successes). Replaying
        // the snapshot on each iteration keeps the per-issue invariant
        // check consistent with what is currently in the DB.
        var existingLinks = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .Select(link => link.IssueId)
            .ToHashSetAsync(StringComparer.Ordinal);

        var outcomes = new List<BatchMembershipOutcome>(dedupByIssueId.Count);
        foreach (var item in dedupByIssueId.Values)
        {
            // Already a member of this epic — idempotent, no duplicate.
            if (existingLinks.Contains(item.IssueId))
            {
                outcomes.Add(BatchMembershipOutcome.AlreadyLinked(item.Identifier, item.IssueId, item.IssueNumber));
                continue;
            }

            // Cross-aggregate uniqueness invariant: an issue may belong to
            // at most one non-terminal epic. Terminal target epics do not
            // consume the active slot, so a target-terminal link ignores
            // the existing-membership ownership check (a conflict on a
            // terminal-target link would be data corruption — the original
            // single-link throws on the same condition; the batch surface
            // mirrors that as a conflict outcome and skips.
            if (!targetIsTerminal
                && await GetActiveMembershipOwnerAsync(db, projectId, item.IssueId, epicId) is { } conflict)
            {
                outcomes.Add(BatchMembershipOutcome.Conflict(
                    item.Identifier, item.IssueId, item.IssueNumber, conflict.EpicId, conflict.Title));
                continue;
            }

            var newLinks = await db.EpicIssues.AsNoTracking()
                .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                .ToListAsync();
            var domain = Materialize(row, newLinks);
            var now = Now();
            domain.LinkIssue(item.IssueId, item.IssueNumber, now.UtcDateTime);

            db.EpicIssues.Add(new EpicIssueRow
            {
                EpicId = epicId,
                ProjectId = projectId,
                IssueId = item.IssueId,
                IssueNumber = item.IssueNumber,
            });
            if (!targetIsTerminal)
            {
                db.EpicActiveIssues.Add(new EpicActiveIssueRow
                {
                    ProjectId = projectId,
                    IssueId = item.IssueId,
                    EpicId = epicId,
                    IssueNumber = item.IssueNumber,
                });
            }
            MapToRow(domain, row, now);
            var pending = DrainPendingEvents(domain);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException) when (!targetIsTerminal)
            {
                // Concurrent claim won the race — surface as conflict and
                // continue with the remaining batch items so the rest of
                // the request still processes.
                db.ChangeTracker.Clear();
                var owner = await GetActiveMembershipOwnerAsync(projectId, item.IssueId, epicId);
                if (owner is not null)
                {
                    outcomes.Add(BatchMembershipOutcome.Conflict(
                        item.Identifier, item.IssueId, item.IssueNumber, owner.EpicId, owner.Title));
                    continue;
                }
                outcomes.Add(BatchMembershipOutcome.Conflict(
                    item.Identifier, item.IssueId, item.IssueNumber, epicId, row.Title));
                continue;
            }

            existingLinks.Add(item.IssueId);
            outcomes.Add(BatchMembershipOutcome.Linked(item.Identifier, item.IssueId, item.IssueNumber));
            await PersistEpicEventsAsync(domain, pending, now);
        }

        return outcomes;
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
        var now = Now();
        domain.UnlinkIssue(issueId, now.UtcDateTime);

        var link = await db.EpicIssues.FirstOrDefaultAsync(
            l => l.ProjectId == projectId && l.EpicId == epicId && l.IssueId == issueId);
        if (link is not null) db.EpicIssues.Remove(link);
        await ReleaseActiveMembershipAsync(db, projectId, epicId, issueId);
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        await db.SaveChangesAsync();
        await PersistEpicEventsAsync(domain, pending, now);
    }

    public async Task<IReadOnlyList<BatchMembershipOutcome>> UnlinkIssuesAsync(
        IReadOnlyList<BatchMembershipRequestItem> issues,
        string projectId)
    {
        if (issues.Count == 0)
            return Array.Empty<BatchMembershipOutcome>();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (row is null) throw new InvalidOperationException($"Epic {epicId} not found");

        // De-duplicate by canonical internal issue id; preserve first
        // identifier for the response.
        var dedupByIssueId = new Dictionary<string, BatchMembershipRequestItem>(StringComparer.Ordinal);
        foreach (var item in issues)
        {
            if (string.IsNullOrWhiteSpace(item.IssueId)) continue;
            dedupByIssueId.TryAdd(item.IssueId, item);
        }
        if (dedupByIssueId.Count == 0)
            return Array.Empty<BatchMembershipOutcome>();

        var existingLinks = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
            .Select(link => link.IssueId)
            .ToHashSetAsync(StringComparer.Ordinal);

        var outcomes = new List<BatchMembershipOutcome>(dedupByIssueId.Count);
        foreach (var item in dedupByIssueId.Values)
        {
            if (!existingLinks.Contains(item.IssueId))
            {
                // Idempotent: not-a-member is a non-error outcome.
                outcomes.Add(BatchMembershipOutcome.WasNotAMember(item.Identifier, item.IssueId, item.IssueNumber));
                continue;
            }

            var links = await db.EpicIssues.AsNoTracking()
                .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                .ToListAsync();
            var domain = Materialize(row, links);
            var now = Now();
            domain.UnlinkIssue(item.IssueId, now.UtcDateTime);

            var link = await db.EpicIssues.FirstOrDefaultAsync(
                l => l.ProjectId == projectId && l.EpicId == epicId && l.IssueId == item.IssueId);
            if (link is not null) db.EpicIssues.Remove(link);
            await ReleaseActiveMembershipAsync(db, projectId, epicId, item.IssueId);
            MapToRow(domain, row, now);
            var pending = DrainPendingEvents(domain);
            await db.SaveChangesAsync();
            existingLinks.Remove(item.IssueId);
            outcomes.Add(BatchMembershipOutcome.Unlinked(item.Identifier, item.IssueId, item.IssueNumber));
            await PersistEpicEventsAsync(domain, pending, now);
        }

        return outcomes;
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
        var now = Now();
        domain.Pause(reason, now.UtcDateTime);
        MapToRow(domain, row, now);
        if (row.Status is EpicStatusName.Done or EpicStatusName.Closed)
            await ReleaseActiveMembershipsAsync(db, projectId, epicId);
        var pending = DrainPendingEvents(domain);
        await db.SaveChangesAsync();
        await PersistEpicEventsAsync(domain, pending, now);
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
        var now = Now();
        var wasAlreadyRunning = row.Status == EpicStatusName.Running;
        domain.Start(now.UtcDateTime);
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        await db.SaveChangesAsync();
        await PersistEpicEventsAsync(domain, pending, now);

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
        var now = Now();
        var wasAlreadyRunning = row.Status == EpicStatusName.Running;
        domain.Resume(now.UtcDateTime);
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        await db.SaveChangesAsync();
        await PersistEpicEventsAsync(domain, pending, now);

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
        if (row.Status is EpicStatusName.Done or EpicStatusName.Closed)
            await ReleaseActiveMembershipsAsync(db, projectId, epicId);
        var pending = DrainPendingEvents(domain);
        await db.SaveChangesAsync();
        await PersistEpicEventsAsync(domain, pending, now);
        return ToDto(row);
    }

    public async Task<EpicDto> ReopenAsync()
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
        // Reopen only accepts terminal epics; non-terminal attempts raise
        // EpicNotTerminalException so the HTTP layer can return 409
        // EPIC_NOT_TERMINAL. EnsureNotTerminal remains in place for the
        // other transitions (Start/Pause/Resume/Done/Close) — Reopen is
        // the single, explicit exit from a terminal state.
        var now = Now();
        domain.Reopen(now.UtcDateTime);
        MapToRow(domain, row, now);

        // Commit the EpicRow state change first. After this, the epic
        // is non-terminal in the DB so any subsequent link/unlink on
        // the active-membership invariant will see us as a valid
        // re-claim candidate.
        var pending = DrainPendingEvents(domain);
        await db.SaveChangesAsync();

        // Re-establish active memberships for each linked issue. The
        // invariant — at most one non-terminal epic actively owns an
        // issue — is enforced by GetActiveMembershipOwnerAsync. A
        // linked issue that was re-homed to another non-terminal epic
        // during the terminal period is silently skipped: its link
        // record stays, the issue is not re-claimed, and reopen does
        // not fail. Each insert is its own save so a duplicate-key
        // race against a concurrent claim surfaces as a per-issue
        // skip rather than rolling back the rest of the re-claim.
        foreach (var link in links)
        {
            var owner = await GetActiveMembershipOwnerAsync(db, projectId, link.IssueId, epicId);
            if (owner is not null) continue;

            db.EpicActiveIssues.Add(new EpicActiveIssueRow
            {
                ProjectId = projectId,
                IssueId = link.IssueId,
                EpicId = epicId,
                IssueNumber = link.IssueNumber,
            });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // A concurrent claim won the race; drop the queued
                // row and move on so the remaining issues still
                // re-claim and the reopen call returns successfully.
                db.ChangeTracker.Clear();
            }
        }

        await PersistEpicEventsAsync(domain, pending, now);
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
        var now = Now();
        domain.Update(title, description, priority, now.UtcDateTime);
        MapToRow(domain, row, now);
        var pending = DrainPendingEvents(domain);
        await db.SaveChangesAsync();
        await PersistEpicEventsAsync(domain, pending, now);
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

        var open = await ComputeOpenLinkedNumbersAsync(db, projectId, links);
        if (open.Count == 0)
        {
            var domain = Materialize(row, links);
            var now = Now();
            domain.MarkDone(open, now.UtcDateTime);
            MapToRow(domain, row, now);
            await ReleaseActiveMembershipsAsync(db, projectId, epicId);
            var pending = DrainPendingEvents(domain);
            await db.SaveChangesAsync();
            await PersistEpicEventsAsync(domain, pending, now);
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
        var open = await ComputeOpenLinkedNumbersAsync(db, projectId, links);
        if (open.Count > 0)
            return ToDto(row);

        var domain = Materialize(row, links);
        var now = Now();
        domain.MarkDone(open, now.UtcDateTime);
        MapToRow(domain, row, now);
        await ReleaseActiveMembershipsAsync(db, projectId, epicId);
        var pending = DrainPendingEvents(domain);
        await db.SaveChangesAsync();
        await PersistEpicEventsAsync(domain, pending, now);
        return ToDto(row);
    }

    private async Task<ActiveMembershipOwner?> GetActiveMembershipOwnerAsync(string projectId, string issueId, string currentEpicId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await GetActiveMembershipOwnerAsync(db, projectId, issueId, currentEpicId);
    }

    private static async Task<ActiveMembershipOwner?> GetActiveMembershipOwnerAsync(
        MohistDbContext db, string projectId, string issueId, string currentEpicId)
    {
        return await (
            from active in db.EpicActiveIssues.AsNoTracking()
            join epic in db.Epics.AsNoTracking()
                on active.EpicId equals epic.Id
            where active.ProjectId == projectId
                && active.IssueId == issueId
                && active.EpicId != currentEpicId
            select new ActiveMembershipOwner(active.EpicId, epic.Title)
        ).FirstOrDefaultAsync();
    }

    private static async Task ReleaseActiveMembershipsAsync(MohistDbContext db, string projectId, string epicId)
    {
        var active = await db.EpicActiveIssues
            .Where(row => row.ProjectId == projectId && row.EpicId == epicId)
            .ToListAsync();
        db.EpicActiveIssues.RemoveRange(active);
    }

    private static async Task ReleaseActiveMembershipAsync(MohistDbContext db, string projectId, string epicId, string issueId)
    {
        var active = await db.EpicActiveIssues.FirstOrDefaultAsync(row =>
            row.ProjectId == projectId && row.EpicId == epicId && row.IssueId == issueId);
        if (active is not null) db.EpicActiveIssues.Remove(active);
    }

    private async Task<HashSet<int>> ComputeOpenLinkedNumbersAsync(
        MohistDbContext db, string projectId, IReadOnlyList<EpicIssueRow> links)
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

    private static bool IsTerminalEpicStatus(string status) =>
        status is EpicStatusName.Done or EpicStatusName.Closed;

    private sealed record ActiveMembershipOwner(string EpicId, string Title);

    private static IReadOnlyList<Epic.Domain.Events.EpicEvent> DrainPendingEvents(EpicAggregate epic)
    {
        var pending = epic.PendingEvents.ToList();
        epic.ClearPendingEvents();
        return pending;
    }

    /// <summary>
    /// Post-commit, best-effort persistence of every domain event the
    /// aggregate recorded since the last drain. Mirrors
    /// <c>IssueGrain.PublishIssueEventsAsync</c>: append each envelope
    /// through <see cref="IEventStore"/> wrapped in try/catch with
    /// <c>_log.LogError</c> on failure (a crash or store error between
    /// state commit and event append loses that mutation's events —
    /// accepted as the timeline is informational and the authoritative
    /// state lives in <c>EpicRow</c>).
    /// </summary>
    private async Task PersistEpicEventsAsync(
        EpicAggregate epic,
        IReadOnlyList<Epic.Domain.Events.EpicEvent> events,
        DateTimeOffset now)
    {
        if (events.Count == 0) return;
        var source = EpicEventPersistence.EpicSource(epic.Id);
        var subject = epic.Number.ToString();
        var extensions = new Dictionary<string, string>
        {
            ["projectid"] = epic.ProjectId,
            ["epicid"] = epic.Id,
            ["epicno"] = subject,
        };

        try
        {
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

                await _eventStore.AppendAsync(envelope, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Post-commit epic event persistence failed for epic {EpicId} ({ProjectId})",
                epic.Id, epic.ProjectId);
        }
    }

    private static EpicDto ToDto(EpicRow epic) =>
        new(epic.Id, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), epic.PauseReason);
}
