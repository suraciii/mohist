## Context

Issue #418 shipped the parent-child relationship — `ParentIssueNumber` on the Issue aggregate, `IssueParentChanged` event, single-level guard, priority inheritance, "is a parent" as a derived fact, and a `ParentHasChildren` start blocker (`packages/server/src/Mohist.Server/Issue/Domain/Issue.Transitions.cs:165`) that today makes `mo issue start <parent>` fail with code `is_parent` (`packages/server/src/Mohist.Server/Api/IssueRoutes.Lifecycle.cs:39`). Nothing drives children, nothing rolls up status, and lifecycle ops on a parent ignore the children.

The accepted product spec (`docs/sub-issues.md`) and design spec (`design/issue-breakdown.md`) already describe the target behavior in product language; `design/workflow/issue-coordination.md:96-99` even sketches the durable reaction `[IssueCompleted / IssueCancelled] → Parent Issue.RecomputeComposite`. This change closes the implementation gap.

The relevant precedents inside the codebase:

- **`EpicGrain`** (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs`) owns its own `RecomputeProgressAsync` entry point and a private `TryStartNextAsync`. Durable handlers in `Events/Subscriptions/EpicAutoDoneHandler.cs` (and siblings) subscribe to issue terminal/reopen/prerequisite-removed events, look up the affected Epic via `EpicQuerier`, and dispatch `RecomputeProgressAsync`. This is the exact shape we mirror for parents — but the parent lookup is direct (the child carries its parent number) instead of a reverse lookup.
- **`IIssueRepositoryCoordinatorGrain`** (`packages/server/src/Mohist.Server/Issue/Grains/Coordinator/`) is a process manager that exists *only* to serialize the create/reassign/reopen/remove-repository commands that can race into orphan repository bindings. `design/architecture.md:103-135` constrains this pattern tightly.
- **`IssueLineage.BuildExtensions`** (`packages/server/src/Mohist.Server/Infrastructure/Data/Issue/IssueLineage.cs:28`) already stamps `projectid`, `issue`, and conditionally `epic` on every Issue CloudEvent — handlers read these via `CloudEventLineage.TryReadIssueContext`.

Constraints inherited from the architecture:

- One transaction writes one aggregate. Parent and child are different Issue aggregates; their coordination goes through durable events, never a shared transaction.
- The Workflow subdomain has zero awareness of parent-child. A parent never owns a `WorkflowRunId`.
- Per-child start gates (draft, prerequisite, repository resolution, runner capacity) cannot be bypassed.

## Goals / Non-Goals

**Goals:**

- `mo issue start <parent>` (and Epic-driven `TryStartFromEpicAsync` on a parent) fires composite advancement: start every startable child in parallel, never mint a workflow run for the parent.
- Auto-continue: child terminal/reopen/attach/detach events drive an idempotent recompute on the parent that starts newly-unlocked children.
- Parent status is a derived fact (Backlog / InProgress / Done / Cancelled), recomputed from a children snapshot, never user-set.
- Parent lifecycle rules: close requires all children terminal, reopen cancelled parent → Backlog, archive cascades to children, child-reopen flips Done parent back to InProgress.
- Epic behavior is unchanged — its existing `TryStartFromEpicAsync` → `StartWorkAsync` path naturally triggers composite advancement; its `IssueCompleted`-driven recompute naturally picks up the parent's aggregated Done.

**Non-Goals** (per `docs/sub-issues.md` and the proposal):

- Parent-side workflow runs, approval gates, retry/rerun/force-stop on the parent.
- Cascade close / cascade cancel of children.
- Cross-repository release coordination.
- Multi-level parent trees.
- Plan-time parent-context injection into child prompts (separate open question in `design/issue-breakdown.md:56`).
- Polished kanban UI (board-card progress badges, blocked indicators) — only minimal detail-page status rendering to satisfy acceptance.

## Decisions

### D1: Recompute lives on `IssueGrain`, not on a new process manager

**Decision.** Add `IIssueGrain.RecomputeCompositeStatusAsync()` and a private `TryStartChildrenAsync(...)` helper. Durable handlers in `Events/Subscriptions/` subscribe to child-side events and dispatch this single grain call to the parent (or both parents, on attach/detach).

**Rationale.** The parent IssueGrain is already a single-activation serialization point for that issue. Each recompute is self-contained: it reads a fresh children snapshot, computes the target status, and applies a no-op-if-already-there transition. There is no uncertain cross-aggregate command delivery state to fence — unlike `IIssueRepositoryCoordinatorGrain`, which exists specifically to fence the create/reassign/reopen/remove commands that can orphan a repository binding. A parent never creates a workflow run, so the orphan-binding invariant that motivates the coordinator does not apply.

**Alternatives considered.**

- *New `IParentIssueCoordinatorGrain` process manager.* Rejected: it would duplicate the EpicGrain shape without adding a serialization property the parent's own grain does not already provide, and `design/architecture.md:103-135` warns against proliferating process managers.
- *Extend `IIssueRepositoryCoordinatorGrain`.* Rejected: that grain's invariant is repository-binding integrity; composite status has nothing to do with repository bindings, and folding both into one fence conflates two unrelated concerns.

### D2: Branch inside `StartWorkAsync`; drop the `ParentHasChildren` blocker

**Decision.** Remove `IssueStartBlocker.ParentHasChildren` entirely. In `IssueGrain.StartWorkAsync`, compute `hasChildren` (the existing `HasChildrenAsync` query) *before* the startability check. If `hasChildren` is true, dispatch to a new `StartCompositeAsync` path; otherwise run the existing per-issue start path unchanged.

`StartCompositeAsync` does, in order, inside the parent grain activation:

1. Load children snapshot via a new `IssueQuerier.ListChildrenForCompositeAsync(projectId, parentNumber)` that returns each child's `Status`, `IsDraft`, `PrerequisiteNumbers`, and the running workflow-run id set.
2. Call the new aggregate transition `Issue.MarkCompositeStarted()` — sets status `InProgress`, does **not** mint a `WorkflowRunId`, does **not** set `_hasWorkflowStarted`, does **not** touch `_repositoryBindingRevision`. Records a new `IssueCompositeStarted` event. Save.
3. For each child that is currently startable (Backlog, not draft, prerequisites all Done, repository declared), call `childGrain.StartWorkAsync()` from the parent grain, in parallel (`Task.WhenAll`), catching per-child failures so a sibling failure does not abort the others. Per-child failures are logged; they will be retried by the next recompute.

The API response is unchanged ("started successfully"); the parent's read model reflects InProgress with no workflow run id.

**Rationale.** Branching inside the existing entry point means `mo issue start`, the API route, and Epic's `TryStartFromEpicAsync` all get composite behavior for free without three call sites. Dropping the `ParentHasChildren` blocker is required: starting a parent is no longer an error. `GetStartReadinessAsync` and the read model's `Blocker` projection (`IssueQuerier.ComputeBlockerForReadModel`) stop returning that variant.

**Alternatives considered.**

- *Separate `StartCompositeAsync` grain entry, route decides.* Rejected: pushes parent-detection into every caller (API, Epic, future callers). The grain is the single authority on "am I a parent?".
- *Keep `ParentHasChildren` blocker, add a separate `/start-composite` route.* Rejected: contradicts the spec ("starting a parent triggers composite advancement"), forces Epic to learn the distinction, and breaks the acceptance criterion that `mo issue start <parent>` works.

### D3: New aggregate transitions for parent-only status changes

**Decision.** Add a small set of parent-only transitions to the `Issue` aggregate, each no-op-if-already-there and each recording a dedicated event. They are *only* legal on a parent (caller passes a non-empty children snapshot; transition throws if the snapshot is empty so a last-child-detach race cannot leave a non-parent in a composite state).

- `MarkCompositeStarted()` — Backlog → InProgress, no workflow run.
- `MarkCompositeDone()` — InProgress → Done, sets `_completedAt`. No `workflowRunId` match (the parent never has one).
- `MarkCompositeCancelled()` — InProgress/Backlog → Cancelled.
- `ReopenComposite()` — Cancelled → Backlog, bypasses the repository-existence check (the parent has no executable target). Used by the close-then-reopen path on a parent.
- `RecomputeCompositeStatus(ChildrenSnapshot)` — pure decision method that picks one of the four target states given a children snapshot; does not mutate. The grain calls it, compares to the current status, and applies the matching transition only when they differ.

Existing `Close` gains an overload `Close(childrenSnapshot, reason)` that throws a typed `IssueParentHasNonTerminalChildrenException` when any child is non-terminal. Existing `Archive` is unchanged on the aggregate; the cascade is a grain-level concern (D6).

**Events** added to `IssueEvent`:

- `IssueCompositeStarted`
- `IssueCompositeStatusChanged(PreviousStatus, NewStatus)` — covers the InProgress→Done, InProgress→Cancelled, Done→InProgress (child reopen), Backlog→InProgress transitions. Single event shape, four legal uses.

We deliberately do **not** add a `PreviousParent`/`NewParent` event — `IssueParentChanged` from #418 already exists and is the trigger for attach/detach recompute (D5).

**Rationale.** Parent transitions have different invariants (no workflow run, no repository binding, no `_hasWorkflowStarted`). Bolting them onto the existing `Start`/`Complete`/`Close` would require parameterizing every call and would blur the contract for normal issues. Separate transitions keep the existing paths crisp.

**Alternatives considered.**

- *Parameterize `Start` with `isComposite`.* Rejected: every existing caller would have to pass `false`; high blast radius for no readability gain.
- *In-line the status math in the grain.* Rejected: the aggregate owns its invariants; the grain is an orchestrator.

### D4: Stamp `parent` in CloudEvent lineage extensions

**Decision.** Extend `IssueLineage.BuildExtensions` (`packages/server/src/Mohist.Server/Infrastructure/Data/Issue/IssueLineage.cs:28`) to stamp `parent = <parentIssueNumber>` whenever `state.ParentIssueNumber` is non-null, exactly mirroring how `epic` is stamped. Extend `CloudEventLineage` with `TryReadParent(extensions, out int parentNumber)`.

**Rationale.** Composite recompute handlers need to know "which parent owns this child". With `parent` stamped on every child event, the handler reads it directly — zero DB lookups in the hot path, identical to how `IssueEpicChangedHandler` reads `epic`. Lineage is purely additive: existing handlers ignore unknown keys.

**Alternatives considered.**

- *Load the child issue inside the handler to read `ParentIssueNumber`.* Rejected: one extra store hit per child event, and the data is already in the producing aggregate's state at event-write time. The lineage channel is the right place for stable cross-aggregate routing keys.

### D5: One durable handler per triggering event, all routing through `parent.RecomputeCompositeStatusAsync()`

**Decision.** Add a single private dispatcher (`ParentCompositeRecomputeDispatcher`) and one handler class per triggering event, paralleling `EpicAutoDoneHandler` / `EpicCancelledHandler` / `EpicIssueReopenedHandler`:

- `IssueCompositeChildStartedHandler` — subscribes to `com.mohist.issue.work-started` (new children that just began work flip parent to InProgress if it is still Backlog).
- `IssueCompositeChildTerminalHandler` — subscribes to `com.mohist.issue.completed` *and* `com.mohist.issue.cancelled`. A child reaching terminal may unlock siblings (start them) and may complete the parent.
- `IssueCompositeChildReopenedHandler` — subscribes to `com.mohist.issue.reopened`. A child reopen on a Done parent flips it back to InProgress.
- `IssueCompositeParentChangedHandler` — subscribes to `com.mohist.issue.parent-changed`. Dispatches recompute to both `PreviousParentIssueNumber` and `ParentIssueNumber` (one will be null on attach/detach; both branches no-op on null). This covers attach (newly-attached child should be evaluated for immediate start), detach (status recompute against remaining children), and last-detach (parent reverts to normal issue — handled by the aggregate's empty-snapshot guard in D3).

Each handler reads `parent` from extensions, calls `parentGrain.RecomputeCompositeStatusAsync()`. The parent grain's recompute is idempotent (D6), so handler redelivery is safe.

We do **not** add handlers for `IssueCreated`, `IssueLabelsChanged`, `IssuePriorityChanged`, etc. — none of those affect aggregation. We also do **not** add a handler for `IssueArchived` — archive cascade is synchronous from the parent grain (D6), not event-driven.

**Rationale.** Mirrors the proven Epic-progress handler topology. Subscribing to the existing child events avoids any change to when those events are published. `IssueParentChanged` already exists from #418, so attach/detach recompute is free.

**Alternatives considered.**

- *Single omnibus handler subscribed to every event.* Rejected: harder to test, harder to reason about idempotency per trigger.
- *Make recompute a periodic sweep.* Rejected: the codebase explicitly moved away from sweeps toward durable triggers (see comments in `EpicPrerequisiteRemovedHandler.cs`).

### D6: Parent recompute is load-fresh-decide-apply, single activation

**Decision.** `RecomputeCompositeStatusAsync` runs entirely inside the parent grain activation, in this order, with no cross-aggregate transaction:

1. Load a fresh children snapshot from `IssueQuerier.ListChildrenForCompositeAsync` (status, terminal-ness, startability inputs). This is a read; the snapshot may be slightly stale — correctness does not depend on it being transactional with the children's state.
2. `var target = _issue.RecomputeCompositeStatus(snapshot);` — pure decision.
3. If `target != _issue.Status`, apply the matching transition from D3 and `SaveIssueAsync`. If the transition is a no-op (already at target), skip the save entirely. This is the idempotency guarantee: redelivery converges.
4. If `target == InProgress` and at least one child is currently startable *and* not yet running, call `TryStartChildrenAsync(startableChildren)` — same fan-out as `StartCompositeAsync`. Per-child failures are logged and left for the next recompute.

The grain activation is the serialization point. Two concurrent recomputes for the same parent queue on the grain; the second one sees the first one's writes (the in-memory `_issue` reflects the saved state after step 3) and either no-ops or re-fans-out idempotently.

Fan-out parallelism is per-child grain: `Task.WhenAll(child.StartWorkAsync())`. Each child grain serializes its own start; the parent does not need to fence them.

**Rationale.** The "load fresh, decide, apply" pattern is exactly what `EpicGrain.RecomputeProgressInternalAsync` does. The aggregate's per-transition guards provide idempotency at the state level; the grain's `SaveIssueAsync` provides durability; the durable handlers provide retry. No new fence is needed.

**Alternatives considered.**

- *Two-phase: compute target, then fan-out, then save.* Rejected: separates state change from the trigger and risks losing the status change if fan-out fails. We save first, then fan-out; fan-out failures are recovered by the next recompute.
- *Snapshot transactional with children.* Rejected: violates the single-aggregate transaction rule. The aggregate's invariants hold for any snapshot; a stale snapshot at worst delays a transition to the next recompute.

### D7: Archive cascade is a synchronous parent-grain loop; new force-archive on the aggregate

**Decision.** `IssueGrain.ArchiveAsync` detects `hasChildren`. If true:

1. Validate the parent itself is archivable (Done, not already archived) via the existing aggregate `Archive()` precondition — fail closed.
2. Enumerate children. For each non-archived child, call `childGrain.ArchiveForParentCascadeAsync()` — a new grain entry that calls a new aggregate transition `ArchiveForced()` which bypasses the `Status == Done` check. This is the only path that uses `ArchiveForced`; direct user archive still requires Done.

The spec scenario "Archiving a parent cascades to children in any terminal state" requires force-archive because Cancelled children fail the normal `Status == Done` guard. The cascade is synchronous (one user action = one cascade); failures are surfaced to the caller.

**Rationale.** The user's single `mo issue archive <parent>` is the unit of work. Event-driven cascade would split it across an unreliable boundary. `ArchiveForced` is narrow (only callable from the cascade grain entry) and tested in isolation.

**Alternatives considered.**

- *Skip Cancelled children in the cascade.* Rejected: violates the spec scenario and leaves orphan non-archived children after parent archive.
- *Allow `Archive()` on Cancelled issues generally.* Rejected: changes normal-issue semantics unnecessarily.

### D8: Parent close and reopen bypass the repository coordinator

**Decision.** The lifecycle routes for `/close` and `/reopen` (`packages/server/src/Mohist.Server/Api/IssueRoutes.Lifecycle.cs:91, 106`) detect parent state via the read model and branch:

- Parent `/close` calls a new `IssueGrain.CloseCompositeAsync()` that loads the children snapshot, calls `Issue.Close(snapshot, reason)` (which throws `IssueParentHasNonTerminalChildrenException` if any child is non-terminal), and saves. Does not flow through `IIssueRepositoryCoordinatorGrain` because no repository binding is at stake.
- Parent `/reopen` calls a new `IssueGrain.ReopenCompositeAsync()` that calls `Issue.ReopenComposite()` and saves. Bypasses the coordinator's `targetExists` check for the same reason — the parent has no executable target.

The typed error codes surface in the API as `parent_has_non_terminal_children` (close) so the Web/CLI can guide the user to deal with each child first.

**Rationale.** The coordinator's fence exists to prevent orphan repository bindings. A parent has no repository binding (no workflow run, no `HasWorkflowStarted`), so the fence would just add latency and a false-failure surface.

**Alternatives considered.**

- *Run parent close/reopen through the coordinator for consistency.* Rejected: coordinator's narrow invariant doesn't apply, and `design/architecture.md:134` explicitly excludes "participants' internal invariant checks" from the coordinator.

### D9: Read-model changes are additive

**Decision.** `IssueReadModel` already carries `ParentIssueRef` and `ChildIssuesSummary` from #418. Extend `ChildIssuesSummary` to include status counts (`Backlog`, `InProgress`, `Done`, `Cancelled`, plus total) so the detail page can render "X/Y done, Z blocked". `IssueQuerier.EnrichAsync` already does the children-count query; extend it to also group by status. The aggregated parent `Status` is persisted on the `IssueRow` like any other issue (the aggregate already moved it via D3's transitions), so the read model needs no special status-derivation logic.

CLI detail output (`packages/cli/Mohist.Cli/MohistCliCommands.Issue*.cs`) renders the aggregated status from the existing `Status` field and the extended child summary. Web detail page (`packages/web/`) shows the same; board-card progress badges are out of scope.

**Rationale.** The read model is a projection of aggregate state. Once the aggregate computes the status (D3) and stores it on `IssueRow.Status` (the existing column), the read model is almost free. The only addition is the per-status children breakdown for the detail page.

### D10: No project-level concurrency limit is introduced

**Decision.** The spec says "concurrency limit applies". The codebase has no project-level concurrency limit today; the closest concept is runner dispatch capacity (per-runner slots, checked in `WorkflowRunQuerier` and `RunnerGrain`). Composite advancement's "do not exceed concurrency" is satisfied by reusing the per-child start path, which already goes through runner capacity admission (`WorkflowRun.EnsureStartedAsync` → runner slot claim). A child that cannot get a slot stays Backlog; the next recompute retries it. We do **not** add a new project-wide cap.

**Rationale.** The acceptance criterion "复合推进启动的子 issue 数量不突破项目并发上限" maps to "don't bypass the existing per-runner capacity gate", which is satisfied by D2/D6's reuse of `StartWorkAsync`. A new project-wide limit would be net-new behavior not described in any spec.

**Alternatives considered.**

- *Add a project-wide max-parallel-issues setting.* Rejected: out of scope, no spec backing, and conflicts with `design/architecture.md`'s "model should be as simple as possible".

## Risks / Trade-offs

- **[Risk] Stale children snapshot during recompute produces a transiently-wrong status.** → *Mitigation:* D6's idempotent re-decide. The next child event redrives the recompute. Correctness is bounded by event delivery latency, not by snapshot transactionality. Tests cover the out-of-order-delivery convergence scenario from `parent-status-aggregation/spec.md`.

- **[Risk] Parent grain activation loss between status change and fan-out.** → *Mitigation:* D6 saves the status change before fan-out. Fan-out failures are recovered by the next recompute, which is delivered by the durable handler on the triggering child event (at-least-once). The worst case is a delayed child start, never a missed one.

- **[Risk] Two parents race when a child is moved between them.** → *Mitigation:* D5 dispatches recompute to both old and new parent. Each parent's grain serializes its own recompute. The child's `IssueParentChanged` event is the single source of truth; the aggregate's `AssignParent` already enforces single-level and backlogged-child invariants atomically with the parent-number change.

- **[Risk] Removing `ParentHasChildren` breaks callers that depend on the `is_parent` blocker code.** → *Mitigation:* The only known consumer is the API response shape in `IssueRoutes.Lifecycle.cs:39`. CLI and Web surfaces render the blocker envelope generically; they do not branch on `is_parent`. Acceptance test: starting a parent succeeds where it previously returned `is_parent`.

- **[Risk] Archive cascade partially succeeds (parent archived, one child fails).** → *Mitigation:* D7's cascade runs in issue-number order and is restartable — the parent's recompute on the next activation will not un-archive, and re-running `mo issue archive <parent>` is idempotent (`ArchiveForced` on an already-archived child is a no-op). Document the recovery procedure in the close path's error message.

- **[Risk] Event lineage gains a new key (`parent`) — older persisted events lack it.** → *Mitigation:* `CloudEventLineage.TryReadParent` returns false for events without the key; the handler simply does not dispatch recompute for those old events (correct — pre-#418 events had no parent relationship anyway). No backfill migration is needed.

- **[Trade-off] Synchronous fan-out inside the parent grain means a `mo issue start <parent>` with many children is a long-running call.** → *Accepted:* each child's `StartWorkAsync` is itself fast (it schedules the workflow run asynchronously via `EnsureStartedAsync`); the runner dispatch happens out-of-band. If this ever becomes a problem, the fan-out can be event-driven, but that adds complexity for no current benefit.

- **[Trade-off] `ArchiveForced` is a narrow escape hatch around the Done-only archive rule.** → *Accepted:* it is only callable from `ArchiveForParentCascadeAsync`, which is only callable from a parent's archive grain entry. The unit test for `ArchiveForced` documents why it exists. A future cleanup might collapse archive eligibility into "terminal" (Done or Cancelled) generally, but that is a separate scope.

## Migration Plan

**Deploy sequence.** Server-only deploy; no schema migration required.

1. **Schema.** No new columns. `IssueRow.Status` already stores the aggregated value (set by D3's transitions). `IssueRow.ParentIssueNumber` from #418 is already present. No backfill.
2. **Event lineage.** The new `parent` extension key starts appearing on issue events after the deploy. Old events in the store simply lack the key; handlers ignore them (D4 risk note).
3. **Existing parents in the wild.** Any issue that is currently a parent (has ≥1 child) but is in a non-Backlog status because of #418's plain-issue lifecycle will be reconciled on the next child event that triggers recompute. To converge immediately on deploy, run `mo issue list --is-parent` (a new minor filter, optional) and `mo issue recompute <number>` for each. If the optional filter is not added, the existing `--parent none`+`--parent <num>` round-trip on any one child triggers a recompute that converges the parent.
4. **API surface.** The `is_parent` blocker code is removed in the same deploy. Clients that special-case `is_parent` (none known in tree) will see the start succeed instead.
5. **Rollback.** Revert the server deploy. Existing parents return to the #418 behavior (starting a parent rejected with `is_parent`). The `IssueCompositeStarted` / `IssueCompositeStatusChanged` events produced during the forward-deploy window are ignored by the pre-#419 handlers (they have no subscribers in the old code). No data corruption.

**Feature flag.** Not required. The change is a single coordinated deploy; the parent-child relationship from #418 already gated the inputs.

## Open Questions

1. **`mo issue list --is-parent` filter and `mo issue recompute <number>` command.** Useful for the post-deploy convergence in the migration plan. Are they in scope for #419, or a follow-up? *Lean:* out of scope; the natural event-driven recompute converges parents on the next child activity. Add them only if acceptance testing reveals a real convergence gap.

2. **Should the `IssueCompositeStatusChanged` event be observable in the Web activity feed?** The Hermes notification dispatcher (`Events/Subscriptions/HermesIssueNotificationHandler.cs`) currently filters by event type. *Lean:* yes — a parent going Done is a user-relevant event — but the exact rendering (which issue, what transition) is a Web UX call deferred to the integrate stage.

3. **What happens to a parent whose every child is detached between recompute fan-out steps?** The D3 transitions throw on an empty children snapshot. The D6 recompute orders "load snapshot → decide → apply → fan-out"; if a detach event lands between load and apply, the next recompute (triggered by `IssueParentChanged`) converges. *Confirm:* this is sufficient; no explicit lock is needed.

4. **`ChildIssuesSummary` shape.** Do we expose per-status counts (`backlog`, `inProgress`, `done`, `cancelled`) or just `done / total / blocked`? *Lean:* per-status counts — they're cheap to compute in the existing `GroupBy` query and let the Web render any future badge without a read-model change. Confirm at integrate time.
