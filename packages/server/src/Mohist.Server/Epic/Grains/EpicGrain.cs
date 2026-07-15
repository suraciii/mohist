using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
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

    public Task LinkIssueAsync(string issueId, int issueNumber, string projectId) =>
        RetryMembershipContentionAsync(async () =>
        {
            await LinkIssueOnceAsync(issueId, issueNumber, projectId);
            return true;
        });

    private async Task LinkIssueOnceAsync(string issueId, int issueNumber, string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parts = GrainKey.Split(':');
        var epicId = parts.Length > 1 ? parts[1] : parts[0];

        var row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId);
        if (row is null) throw new InvalidOperationException($"Epic {epicId} not found");

        var alreadyLinkedToThisEpic = await db.EpicIssues.AsNoTracking()
            .AnyAsync(link => link.ProjectId == projectId && link.EpicId == epicId && link.IssueId == issueId);
        if (alreadyLinkedToThisEpic) return;

        // Closed is a hard-stop for new links: the domain LinkIssue guard
        // throws EpicClosedCannotLinkException before any row is added, and
        // the HTTP layer maps it to 409 EPIC_CLOSED_CANNOT_LINK.
        if (row.Status == EpicStatusName.Closed)
        {
            throw new EpicClosedCannotLinkException(epicId);
        }

        // Cross-aggregate uniqueness invariant: an issue may belong to at
        // most one non-terminal epic (idle/running/paused). For a done
        // target, the wake below flips the epic to running, so the active
        // row we are about to insert will be a real non-terminal owner;
        // the check therefore MUST run before the wake-up so we never
        // create a duplicate row against another non-terminal epic.
        if (await GetActiveMembershipOwnerAsync(db, projectId, issueId, epicId) is { } conflict)
        {
            throw new InvalidOperationException(
                $"Issue already belongs to Epic '{conflict.EpicId}' ({conflict.Title})");
        }

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

        // Done + open issue wakes the epic to running in the same commit
        // as the link row; the active-membership row is also created in
        // that commit so autopilot sees the newly linked issue.
        var wakeUpEpic = row.Status == EpicStatusName.Done
            && await IsIssueOpenAsync(db, projectId, issueNumber);
        if (wakeUpEpic)
        {
            domain.WakeFromDone(now.UtcDateTime);
        }

        if (row.Status != EpicStatusName.Done || wakeUpEpic)
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
        await StageIssueLineageSnapshotAsync(db, issueId, epicId);
        var pending = DrainPendingEvents(domain);
        // Append the EpicIssueLinked event into the same transaction so the
        // durable recompute trigger (EpicIssueLinkedHandler) is committed
        // atomically with the membership row — a crash or failed append
        // between commit and event persistence would otherwise lose the only
        // convergence path for a link whose inline recompute never ran.
        await PersistEpicEventsAsync(db, domain, pending, now);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            var owner = await GetActiveMembershipOwnerAsync(projectId, issueId, epicId);
            if (owner is not null)
            {
                throw new InvalidOperationException(
                    $"Issue already belongs to Epic '{owner.EpicId}' ({owner.Title})");
            }
            throw;
        }

        // Refresh the active Issue aggregate after the committed scalar
        // snapshots make subsequent Issue and Workflow appends atomic.
        await PushEpicAffiliationAsync(issueId, projectId, "link");

        if (wakeUpEpic)
        {
            await TryStartNextAsync(db, projectId, epicId, row, await db.EpicIssues.AsNoTracking()
                .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                .ToListAsync());
        }
        else if (row.Status != EpicStatusName.Done && row.Status != EpicStatusName.Closed)
        {
            // Link-time recompute: after a non-wake link to a non-terminal
            // epic, recompute progress so a startable issue linked to a
            // running epic is advanced via TryStartNext, and an epic whose
            // members are all complete at link time is marked done. This
            // preserves the readiness behavior previously supplied by the
            // poll-driven sweep, deleted in #363.
            await RecomputeProgressInternalAsync(db, projectId, epicId, row,
                await db.EpicIssues.AsNoTracking()
                    .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                    .ToListAsync(),
                StartFailureMode.PreserveRunning);
        }
    }

    public Task<IReadOnlyList<BatchMembershipOutcome>> LinkIssuesAsync(
        IReadOnlyList<BatchMembershipRequestItem> issues,
        string projectId) =>
        RetryMembershipContentionAsync(() => LinkIssuesOnceAsync(issues, projectId));

    private async Task<IReadOnlyList<BatchMembershipOutcome>> LinkIssuesOnceAsync(
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

        // Closed is a hard-stop for the whole batch: the domain LinkIssue
        // guard throws EpicClosedCannotLinkException before any row is
        // added, and the HTTP layer maps it to 409 EPIC_CLOSED_CANNOT_LINK.
        // Rejecting before the loop (rather than per-item) matches the
        // spec scenario "Batch link to a closed epic is rejected as a
        // whole" — no per-item outcomes are produced.
        if (row.Status == EpicStatusName.Closed)
        {
            throw new EpicClosedCannotLinkException(epicId);
        }

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

        // Tracks whether THIS batch woke the epic from done to running
        // (per the live row.Status observation). Once flipped, MapToRow
        // updates row.Status in memory so subsequent items in the same
        // batch take the normal non-terminal path — preventing a second
        // WakeFromDone call (which would throw on a non-done epic).
        var wasDoneAtEntry = row.Status == EpicStatusName.Done;

        var outcomes = new List<BatchMembershipOutcome>(dedupByIssueId.Count);
        // Tracks whether at least one item in this batch was actually
        // linked (not deduped, not rejected by the cross-aggregate
        // uniqueness check, not lost to a save-conflict). The link-time
        // recompute below only fires when a link was committed; a
        // batch where every item was rejected must not trigger a
        // MarkDone event from the recompute.
        var hasLinkedAny = false;
        foreach (var item in dedupByIssueId.Values)
        {
            // Already a member of this epic — idempotent, no duplicate.
            if (existingLinks.Contains(item.IssueId))
            {
                outcomes.Add(BatchMembershipOutcome.AlreadyLinked(item.Identifier, item.IssueId, item.IssueNumber));
                continue;
            }

            // Live per-item decision: row.Status is refreshed by MapToRow
            // after every commit, so the second item sees the wake-up
            // applied by the first and takes the non-terminal branch
            // without re-invoking WakeFromDone. Per design D4 we do NOT
            // pre-classify the batch or wake before any durable commit.
            var targetIsTerminal = IsTerminalEpicStatus(row.Status);
            var wakeUpEpic = row.Status == EpicStatusName.Done
                && await IsIssueOpenAsync(db, projectId, item.IssueNumber);

            // Cross-aggregate uniqueness invariant: an issue may belong to
            // at most one non-terminal epic. For a done + open link, the
            // wake-up below flips the epic to running, so the active row
            // we are about to insert will be a real non-terminal owner —
            // the check therefore MUST run before the wake-up so we never
            // create a duplicate row against another non-terminal epic.
            // For done + terminal-issue links and for non-terminal targets,
            // we follow the same rule: only check when an active row would
            // actually be inserted.
            var willInsertActiveRow = wakeUpEpic || !targetIsTerminal;
            if (willInsertActiveRow
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

            // Done + open issue wakes the epic to running in the same
            // commit as the link row; the active-membership row is also
            // created in that commit so autopilot sees the new open work.
            if (wakeUpEpic)
            {
                domain.WakeFromDone(now.UtcDateTime);
            }

            if (willInsertActiveRow)
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
            var affiliation = willInsertActiveRow
                ? epicId
                : await EpicIssueAffiliationResolver.ResolveAsync(db, projectId, item.IssueId);
            await StageIssueLineageSnapshotAsync(db, item.IssueId, affiliation);
            var pending = DrainPendingEvents(domain);
            // Append EpicIssueLinked atomically with the membership row so
            // the durable recompute trigger is never lost on crash/append
            // failure (same rationale as the single-link path above).
            await PersistEpicEventsAsync(db, domain, pending, now);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                throw;
            }
            catch (DbUpdateException) when (willInsertActiveRow)
            {
                // Concurrent claim won the race — surface as conflict and
                // continue with the remaining batch items so the rest of
                // the request still processes. Re-fetch the epic row so
                // subsequent iterations see the committed DB state (not the
                // detached in-memory snapshot left by ChangeTracker.Clear()).
                db.ChangeTracker.Clear();
                row = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Id == epicId)
                    ?? throw new InvalidOperationException($"Epic {epicId} not found");
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
            hasLinkedAny = true;

            // Refresh the active Issue aggregate after the scalar snapshots
            // commit with this membership change.
            await PushEpicAffiliationAsync(item.IssueId, projectId, "link");
        }

        // Per design D4: if this batch woke the epic from done to running,
        // invoke TryStartNextAsync exactly once so autopilot advances the
        // newly linked open issue with no caller-issued start. The live
        // row.Status check at the top of TryStartNextAsync is a no-op for
        // non-running epics; the wasDoneAtEntry gate guards the intent.
        if (wasDoneAtEntry && row.Status == EpicStatusName.Running)
        {
            var finalLinks = await db.EpicIssues.AsNoTracking()
                .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                .ToListAsync();
            await TryStartNextAsync(db, projectId, epicId, row, finalLinks);
        }
        else if (hasLinkedAny && !wasDoneAtEntry && row.Status != EpicStatusName.Done && row.Status != EpicStatusName.Closed)
        {
            // Link-time recompute for non-wake batches to non-terminal
            // epics: covers startable-issue-linked-to-running-epic
            // (TryStartNext advance) and all-complete-at-link-time
            // (MarkDone). Preserves the readiness behavior previously
            // supplied by the poll-driven sweep, deleted in #363. Only
            // fires when at least one item in the batch was actually
            // linked — a batch where every item was rejected must not
            // trigger a MarkDone event from the recompute.
            var finalLinks = await db.EpicIssues.AsNoTracking()
                .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                .ToListAsync();
            await RecomputeProgressInternalAsync(db, projectId, epicId, row, finalLinks,
                StartFailureMode.PreserveRunning);
        }

        return outcomes;
    }

    public Task UnlinkIssueAsync(string issueId, string projectId) =>
        RetryMembershipContentionAsync(async () =>
        {
            await UnlinkIssueOnceAsync(issueId, projectId);
            return true;
        });

    private async Task UnlinkIssueOnceAsync(string issueId, string projectId)
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
        var affiliationAfterUnlink = await EpicIssueAffiliationResolver.ResolveAsync(
            db, projectId, issueId, excludedEpicId: epicId);
        await StageIssueLineageSnapshotAsync(db, issueId, affiliationAfterUnlink);
        var pending = DrainPendingEvents(domain);
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();
        // Refresh the active Issue aggregate after the scalar snapshots
        // commit with this membership change.
        await PushEpicAffiliationAsync(issueId, projectId, "unlink");
        await RecomputeProgressInternalAsync(
            db,
            projectId,
            epicId,
            row,
            await db.EpicIssues.AsNoTracking()
                .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                .ToListAsync(),
            StartFailureMode.PreserveRunning);
    }

    public Task<IReadOnlyList<BatchMembershipOutcome>> UnlinkIssuesAsync(
        IReadOnlyList<BatchMembershipRequestItem> issues,
        string projectId) =>
        RetryMembershipContentionAsync(() => UnlinkIssuesOnceAsync(issues, projectId));

    private async Task<IReadOnlyList<BatchMembershipOutcome>> UnlinkIssuesOnceAsync(
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
        var hasUnlinkedAny = false;
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
            var affiliationAfterUnlink = await EpicIssueAffiliationResolver.ResolveAsync(
                db, projectId, item.IssueId, excludedEpicId: epicId);
            await StageIssueLineageSnapshotAsync(db, item.IssueId, affiliationAfterUnlink);
            var pending = DrainPendingEvents(domain);
            await PersistEpicEventsAsync(db, domain, pending, now);
            await db.SaveChangesAsync();
            existingLinks.Remove(item.IssueId);
            outcomes.Add(BatchMembershipOutcome.Unlinked(item.Identifier, item.IssueId, item.IssueNumber));
            hasUnlinkedAny = true;

            // Refresh the active Issue aggregate after the scalar snapshots
            // commit with this membership change.
            await PushEpicAffiliationAsync(item.IssueId, projectId, "unlink");
        }

        if (hasUnlinkedAny)
        {
            await RecomputeProgressInternalAsync(
                db,
                projectId,
                epicId,
                row,
                await db.EpicIssues.AsNoTracking()
                    .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                    .ToListAsync(),
                StartFailureMode.PreserveRunning);
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
        // Persist the status-changed event atomically with the state transition
        // so the EpicRunningStatusHandler durable trigger survives or rolls back
        // together. The handler re-drives recompute if the command-path
        // TryStartNextAsync never runs (crash after commit).
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();

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
        await PersistEpicEventsAsync(db, domain, pending, now);
        await db.SaveChangesAsync();

        if (row.Status == EpicStatusName.Running && !wasAlreadyRunning)
        {
            return await RecomputeProgressInternalAsync(db, projectId, epicId, row, links);
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

        // Re-establish active memberships in the same database commit
        // as the terminal-to-idle transition. If that commit fails, the
        // epic remains terminal and a retry can perform the full reopen
        // again instead of getting stuck in an idle-without-active-rows
        // partial state.
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
        }

        var pending = DrainPendingEvents(domain);
        await db.SaveChangesAsync();
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

    public async Task<EpicDto?> RecomputeProgressAsync()
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

        return await RecomputeProgressInternalAsync(db, projectId, epicId, row, links, StartFailureMode.Propagate);
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
        MohistDbContext db, string projectId, string epicId, EpicRow row, IReadOnlyList<EpicIssueRow> links,
        StartFailureMode startFailureMode = StartFailureMode.PreserveRunning)
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
            return await TryStartNextAsync(db, projectId, epicId, row, links, startFailureMode);
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
        MohistDbContext db, string projectId, string epicId, EpicRow row, IReadOnlyList<EpicIssueRow> links,
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
            var issueGrain = _grains.GetGrain<IIssueGrain>(Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(next.Id));
            await issueGrain.StartWorkAsync();
        }
        catch (Exception ex)
        {
            if (startFailureMode == StartFailureMode.PreserveRunning)
            {
                _log.LogWarning(ex,
                    "Epic {EpicId} ({ProjectId}) failed to start next linked issue {IssueId}; epic remains running-but-idle, emitting start-attempt-failed for durable retry",
                    epicId, projectId, next.Id);
                var now = _timeProvider.GetUtcNow();
                var domain = Materialize(row, links);
                domain.RecordStartAttemptFailure(next.Id, next.Number, "start-failed", now.UtcDateTime);
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

    private static async Task<bool> IsIssueOpenAsync(MohistDbContext db, string projectId, int issueNumber)
    {
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number == issueNumber)
            .ToListAsync();
        var byNumber = IssueRowMapper.ByNumber(rows, projectId, new[] { issueNumber });
        if (!byNumber.TryGetValue(issueNumber, out var issue)) return false;
        return issue.Status is not (IssueStatus.Done or IssueStatus.Cancelled);
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
    /// aggregate recorded since the last drain. Epic event persistence is
    /// intentionally still outside the transaction for this issue: a crash or
    /// store error between state commit and event append loses that mutation's
    /// events, while authoritative epic state remains in <c>EpicRow</c>.
    /// </summary>
    private async Task PersistEpicEventsAsync(
        EpicAggregate epic,
        IReadOnlyList<Epic.Domain.Events.EpicEvent> events,
        DateTimeOffset now)
    {
        if (events.Count == 0) return;
        var source = EpicEventPersistence.EpicSource(epic.Id);
        var subject = epic.Number.ToString();
        var extensions = EpicLineage.BuildExtensions(epic);

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

    /// <summary>
    /// Atomically persists events into the caller's <paramref name="db"/>
    /// context, so they survive or roll back with the epic state transition
    /// in the same <c>SaveChangesAsync</c> call. Used for recovery-critical
    /// events (<c>EpicIssueLinked</c>, <c>EpicStartAttemptFailed</c>) whose
    /// loss would leave an epic stuck running-but-idle with no durable
    /// recompute trigger.
    /// </summary>
    private async Task PersistEpicEventsAsync(
        MohistDbContext db,
        EpicAggregate epic,
        IReadOnlyList<Epic.Domain.Events.EpicEvent> events,
        DateTimeOffset now)
    {
        if (events.Count == 0) return;
        var source = EpicEventPersistence.EpicSource(epic.Id);
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
        new(epic.Id, epic.Number, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), epic.PauseReason);

    private async Task<T> RetryMembershipContentionAsync<T>(Func<Task<T>> operation)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                _log.LogInformation(
                    "Epic membership lineage update conflicted on attempt {Attempt}; retrying from persisted state",
                    attempt);
            }
        }
    }

    private static async Task StageIssueLineageSnapshotAsync(
        MohistDbContext db,
        string issueId,
        string? epicId)
    {
        var workflowRunId = await IssueStore.StageEpicAffiliationAsync(db, issueId, epicId);
        await WorkflowRunStore.StageEpicAffiliationAsync(db, workflowRunId, epicId);
    }

    /// <summary>
    /// Refreshes an active Issue aggregate from the committed lineage
    /// snapshot. Membership transactions already update the Issue and
    /// WorkflowRun snapshots atomically, so a failed refresh cannot make
    /// emitted envelopes stale; durable handlers retry it for active state.
    /// </summary>
    private async Task PushEpicAffiliationAsync(string issueId, string projectId, string operation)
    {
        string? epicId = null;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            epicId = await EpicIssueAffiliationResolver.ResolveAsync(db, projectId, issueId);
            var issueGrain = _grains.GetGrain<IIssueGrain>(Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(issueId));
            await issueGrain.SetEpicAffiliationAsync(epicId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Epic {Operation} denormalization push failed for issue {IssueId} (target epic {EpicId}); durable handler will re-apply",
                operation, issueId, epicId ?? "<null>");
        }
    }
}
