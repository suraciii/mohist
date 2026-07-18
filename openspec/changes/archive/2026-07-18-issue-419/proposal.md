## Why

Issue #418 delivered the parent-child relationship itself — `--parent`, single-level constraint, priority inheritance, "is a parent" as a derived fact — but stopped short of letting a parent actually drive its children: starting a parent is today rejected outright with a `ParentHasChildren` blocker (`packages/server/src/Mohist.Server/Issue/Domain/Issue.Transitions.cs:165`), the parent's status is whatever a normal issue would have, and lifecycle operations like close/archive/reopen ignore the children entirely. That leaves the user to start each child by hand, watch each one finish, and then mentally roll up progress — exactly the overhead the parent-child model exists to remove. The product spec (`docs/sub-issues.md`) and design spec (`design/issue-breakdown.md`) for composite advancement and status aggregation are already accepted; this change closes the implementation gap.

## What Changes

- **Starting a parent triggers composite advancement**: `mo issue start <parent>` (or an Epic-driven `TryStartFromEpicAsync` on a parent) no longer returns the `is_parent` blocker. Instead it starts every startable child (Backlog + prerequisites satisfied) **in parallel**, each in its own target repository with its own workflow run. The parent never owns a workflow run.
- **Auto-continuation**: whenever a child reaches a terminal state (Done or Cancelled), the parent re-evaluates and starts any newly-unlocked children (e.g. those whose prerequisite just got delivered), until every child is terminal. This reuses the durable event → handler → idempotent command pattern already used by Epic recompute.
- **Status aggregation** for the parent (derived, never user-set):
  - `backlog` — not yet started and no child has begun work.
  - `in-progress` — composite advancement started, or any child began work.
  - `done` — all children terminal and at least one Done. **BREAKING** for any caller that reads the parent's status today, since a parent can no longer be cancelled by itself.
  - `cancelled` — all children Cancelled.
- **Lifecycle rules for parents** — **BREAKING** vs. today's plain-issue behavior, all enforced by the aggregate using the same children snapshot:
  - `close` on a parent is rejected unless **all** children are terminal (no cascade close).
  - `reopen` on a cancelled parent returns it to `backlog`; the user can attach new children and start again.
  - `archive` on a parent cascades: every non-terminal child is archived together with the parent.
  - `--parent none` (detach a child) immediately recomputes the parent's aggregated status; when the last child is detached the parent reverts to a normal issue (already true in #418 for the "is a parent" fact — this change makes its **status** follow).
  - a Done parent auto-flips back to `in-progress` when any child is reopened.
- **Concurrency**: composite advancement does not bypass the existing start-time gates (draft, prerequisite, repository resolution, runner dispatch capacity). A child that cannot acquire a runner slot stays Backlog and is retried on the next recompute; the parent's `in-progress` status does not require every child to be simultaneously running.
- **Epic isolation preserved (acceptance-only)**: an Epic that links a parent issue continues to treat it as a normal member — `TryStartFromEpicAsync` kicks off composite advancement, the parent's eventual `done` counts toward Epic progress. No Epic code changes; this is verified, not implemented.
- **Out of scope**: parent-side workflow/approval gates, cross-repository release coordination, multi-level trees, plan-time parent-context injection (all still deferred per `docs/sub-issues.md`).

## Capabilities

- `compound-advancement`: How a parent's start drives its children's starts. Covers the startable-child selection (Backlog + prerequisites satisfied), parallel fan-out on `mo issue start <parent>` and on Epic-driven start, auto-continuation when a child goes terminal (Done/Cancelled unlocks the next batch), the "parent never owns a workflow run" invariant, and the rule that concurrency gates (runner capacity, prerequisite, draft, repository) apply per-child exactly as on a manual child start.
- `parent-status-aggregation`: How the parent's lifecycle and status are derived from its children. Covers the four-state aggregation table (backlog / in-progress / done / cancelled), close/reopen/archive/detach semantics for a parent, the child-reopen → parent-back-to-in-progress rule, and the recompute triggers (child terminal/reopen/parent-changed events) that keep the derived status eventually consistent.

## Impact

- **Server — Issue domain (`packages/server/src/Mohist.Server/Issue/Domain/`)**:
  - `Issue.Transitions.cs` — the `StartBlocker` `ParentHasChildren` path is replaced by a composite-advancement entry on the parent; new transitions for aggregate-driven status changes (`MarkParentInProgress`, `MarkParentDone`, `MarkParentCancelled`, `ReopenParent`); `Close`/`Archive`/`Reopen` gain parent-aware guards that consult the children snapshot.
  - `IssueStartBlocker.cs` — `ParentHasChildren` is removed (or repurposed) since starting a parent is now the trigger, not a blocker.
  - `Events/IssueEvent.cs` — new events for parent status changes (e.g. `IssueParentStatusRecomputed`), and child-detached/child-terminal events already exist (`IssueCompleted`, `IssueCancelled`, `IssueReopened`, `IssueParentChanged`) and are reused as recompute triggers.
- **Server — Issue grain (`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs`)**:
  - `StartWorkAsync` / `ThrowIfStartBlocked` — when the issue is a parent, dispatch to a new composite-advancement path that selects startable children and calls each child grain's `StartWorkAsync`, surfacing per-child failures without aborting siblings.
  - New grain entry point (or per-parent process-manager grain) for status recompute, invoked by durable handlers on child terminal/reopen/parent-changed events. Decides whether a coordinator-style serial manager is needed, following `design/architecture.md`'s "durable application process manager" constraints.
- **Server — Issue coordinator (`packages/server/src/Mohist.Server/Issue/Grains/Coordinator/`)**: evaluate whether composite fan-out and status recompute must flow through `IssueRepositoryCoordinatorGrain` for serial-retry safety, or whether per-child issue grains + durable events suffice (the parent never creates a workflow run, so repository-binding invariants are not directly at stake).
- **Server — Event subscriptions (`packages/server/src/Mohist.Server/Events/Subscriptions/`)**: new handlers that turn `IssueCompleted` / `IssueCancelled` / `IssueReopened` / `IssueParentChanged` into a recompute command on the affected parent grain(s), paralleling `EpicAutoDoneHandler` and friends. Handlers live in the Issue subdomain (Epic handlers stay untouched — the parent looks like a normal issue to Epic).
- **Server — Issue read model (`packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`, `IssueReadModel.cs`, `IssueRow.cs`)**: parent issues already project `ParentIssueNumber`/child count from #418; add a children-status summary (X done / Y total / Z blocked) and ensure `Status` reflects the aggregated value, not a manually-set one.
- **Server — API (`packages/server/src/Mohist.Server/Api/IssueRoutes.Lifecycle.cs`)**: the `is_parent` blocker code at `IssueRoutes.Lifecycle.cs:39` is removed (or redefined) since the start route now succeeds for parents; close/archive/reopen routes surface the new parent-aware rejections with typed codes.
- **CLI (`packages/cli/Mohist.Cli/MohistCliCommands.Issue*.cs`)**: no new flags; `mo issue start <parent>` becomes the composite-advancement entry. Detail/list output should render the aggregated status and (minimally) child progress.
- **Web (`packages/web/`)**: parent detail already shows the parent/child relationship from #418; this change updates the rendered status to the aggregated value. Board-card progress badges and blocked indicators are polished only if needed to satisfy acceptance; full kanban UI polish stays deferred.
- **Specs**: introduces `openspec/changes/issue-419/specs/compound-advancement/spec.md` and `openspec/changes/issue-419/specs/parent-status-aggregation/spec.md`. The existing `openspec/specs/issue-parent-child/` (relationship) spec from #418 is the prerequisite and is not modified.
- **Dependencies**: none new; reuses existing durable dispatcher, event store, and per-child `StartWorkAsync` path.
- **Risk**: medium — issue lifecycle semantics change (parent status is now derived), and composite advancement must coexist correctly with Epic auto-advance (which already calls the parent's start path). Mitigated by the accepted design spec, the durable event → handler → idempotent-command pattern already proven by Epic recompute, and the constraint that the parent never owns a workflow run (so workflow invariants are unaffected).
