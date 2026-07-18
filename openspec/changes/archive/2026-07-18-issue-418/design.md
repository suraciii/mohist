## Context

Issue #418 introduces a one-level parent-child (sub-issue) relationship on the Issue domain. The product spec ([`docs/sub-issues.md`](../../docs/sub-issues.md)) and the accepted domain design ([`design/issue-breakdown.md`](../../design/issue-breakdown.md)) already fix the model: a child holds a single-direction reference to its parent; "parent" is the derived fact of having ≥1 child; the Workflow subsystem stays zero-awareness; Epic machinery is unchanged. #418 delivers only **establish, remove, and validate** the relationship plus the "a parent cannot start its own workflow" guard. Composite advancement (start parent ⇒ fan out to children), status aggregation (recompute parent from child terminal/reopen events), and the parent close/archive constraints from the accepted design's lifecycle table are explicitly later issues.

The implementation mirrors two existing patterns in the Issue aggregate:

- **`EpicNumber?` affiliation** (`Issue.cs:95`, `AssignEpic`/`RemoveEpic`/`ChangeEpic` at `Issue.Transitions.cs:218-240`) — single-direction reference on the Issue, derived membership by query, transition records `IssueEpicChanged` in the same transaction.
- **`StartBlocker`** (`Issue.Transitions.cs:160`) — the aggregate decides from facts the grain gathers (`undeliveredPrerequisites`); typed `IssueStartBlocker` variants are mapped to codes at `IssueRoutes.Lifecycle.cs:36-41`.

Stakeholders: Issue subdomain (owner), Epic subdomain (untouched per accepted design), CLI surface, Web read surfaces. Workflow subsystem has no stake (zero-awareness invariant).

## Goals / Non-Goals

**Goals:**
- Add a `ParentIssueNumber?` affiliation to the Issue aggregate, symmetric in shape to `EpicNumber?`.
- Establish the link via `mo issue create --parent` and `mo issue update --parent`; remove it via `--parent none`.
- Enforce every validation the spec lists: single-level; Backlog-only for both gaining and becoming a child; self-parent and missing-parent rejection; priority inheritance on create; bidirectional Epic isolation; parent-start refusal via a typed blocker.
- Project the relationship on both read sides (child's parent ref, parent's children summary) and support `mo issue list --parent`.

**Non-Goals:**
- Composite advancement (starting a parent by driving its children). `mo issue start` on a parent is rejected outright for now.
- Status aggregation / `RecomputeComposite` handler. The event topology slot at `design/workflow/issue-coordination.md:97` is reserved but not wired.
- The accepted design's parent close/archive constraints ("close parent needs all children terminal", "archive cascades to children"). Those belong with composite advancement.
- Auto-split suggestions, multi-level trees, plan-context injection from parent, Web board progress badges.

## Decisions

### Decision 1: Store the parent reference on the child, mirror `EpicNumber?`

Add `int? ParentIssueNumber` to `Issue` (backing field `_parentIssueNumber`, init-only property normalizing `> 0` to null otherwise), exactly as `EpicNumber` does at `Issue.cs:95`. "Is a parent" and "children of N" are **derived by query** over `IssueRow.ParentIssueNumber`, never a second list stored on the parent.

- **Alternative considered:** parent holds a `_childNumbers[]`. Rejected — it duplicates the child's own fact, violating the single-writer-authority rule from `design/workflow/issue-coordination.md:9-21` (the Issue is the sole authority for its own affiliations) and creating a two-copy drift problem the Epic design already avoids.
- **Consequence:** the parent never mutates when a child attaches/detaches; only the child does. The parent's "is a parent" fact is recomputed at read/start time. This is the same trade-off Epic makes and is what keeps the Epic machinery untouched.

### Decision 2: Validation split — self-invariants in the aggregate, cross-aggregate facts gathered by the grain

- **Self-invariants (atomic, in `Issue.AssignParent`/`RemoveParent`/`AssignEpic`):** the target is not in an Epic when becoming a child; a child is not being made an Epic member; self-parenting (`parent == self`); the target is Backlog and has not started a workflow (`!_hasWorkflowStarted && _status == Backlog`); idempotent detach. These throw domain exceptions, mirroring `AssignEpic`'s style.
- **Cross-aggregate facts (grain reads, then issues the transition):** the designated parent exists; the parent is itself Backlog/not-started; the parent is not already a child (single-level); the target does not currently have children (a parent cannot become a child). The `IssueGrain` loads the parent grain and queries children before calling `_issue.AssignParent(...)`, the same way `EpicGrain.LinkIssueAsync` reads the Issue before issuing `AssignEpic` (`design/workflow/issue-coordination.md:26-37`).
- **"Has started a workflow" guard uses `_hasWorkflowStarted`**, not `_status == InProgress`. The accepted design's table says "子必须 backlog 未启动"; the durable `HasWorkflowStarted` flag (`Issue.cs:150`) is the correct reading of "未启动" and prevents a Reopened issue from being attached.

### Decision 3: Epic isolation is enforced as self-invariants on Issue, not by touching Epic

- `Issue.AssignEpic` rejects when `_parentIssueNumber is not null` (a child cannot join an Epic).
- `Issue.AssignParent` rejects when `_epicNumber is not null` (an Epic member cannot become a child).

Both are pure self-checks — the child knows both its own parent and its own Epic — so no cross-aggregate coordination is needed and the Epic domain/grain logic is genuinely untouched, satisfying "Epic 机制零改动" from `design/issue-breakdown.md:41`. The only Epic-side change is at the **route catch layer** (`EpicRoutes.cs:70`, next to `EpicClosedCannotLinkException`): map the new `IssueChildCannotJoinEpicException` to a typed 409 code so `mo epic link <epic> <child>` surfaces a distinguishable rejection.

### Decision 4: Extend `IssueStartBlocker` with a `ParentHasChildren` variant

- Add `IssueStartBlocker.ParentHasChildren` to the discriminated union (`IssueStartBlocker.cs:3`).
- `Issue.StartBlocker` gains a `bool hasChildren` parameter alongside the existing `undeliveredPrerequisites`. The `IssueGrain` computes `hasChildren` via `IssueQuerier` (count of issues with `ParentIssueNumber == Number`) before calling `ThrowIfStartBlocked`, exactly as it already gathers undelivered prerequisites (`IssueGrain.cs:153-154`).
- Map the new variant to a distinct code (e.g. `is_parent`) in the switch at `IssueRoutes.Lifecycle.cs:36-41`, alongside `draft` / `waiting_for_prerequisite`.
- **Alternative considered:** refuse in the grain without a blocker variant. Rejected — the existing typed-blocker envelope is what lets the CLI and Web render start refusals consistently; reusing it keeps the contract stable and gives the later composite-advancement issue a single seam to amend (it will replace `ParentHasChildren` rejection with fan-out).

### Decision 5: Priority inheritance is resolved by the create orchestration, not the aggregate

`Issue.Create` already takes a resolved `priority` string and a resolved repository (`Issue.Transitions.cs:22` requires the repository to be resolved before Create). Inheritance follows the same shape: the create path (route → grain / creation service) reads the parent's priority when `--parent` is supplied and `--priority` is absent, and passes the resolved value to `Issue.Create`. The aggregate stays unaware of inheritance.

### Decision 6: Projection — add a column, backfill null, derive at read time

- `IssueRow` gains `ParentIssueNumber` (mirrors `EpicNumber` at `IssueRow.cs:29`). The aggregate's persisted state gains the matching field.
- `IssueReadModel` gains `ParentIssueRef?` (number + title for the child's detail) and a `ChildIssuesSummary?` (count + minimal child info for the parent's detail). The parent ref is the child's own fact; the children summary is computed by `IssueQuerier` at read time — it is a projection, never a second source of truth.
- `mo issue list --parent N` is a new filter in `IssueQuerier` (`where IssueRow.ParentIssueNumber == N`), paralleling the existing `epicNumber`/`label`/`stage` filters.
- Migration: additive `ALTER TABLE` + null backfill, structurally identical to `20260715000000_BackfillIssueEpicAffiliation.cs`. No data to transform.

### Decision 7: CLI wiring reuses existing flag plumbing

- `mo issue create` / `mo issue update` gain `--parent <number>` (with `none` as the detach sentinel on update, parsed through the same three-state `Fields` set `UpdateIssueRequest.BindAsync` already uses at `IssueRoutes.Dtos.cs:83`). The CLI flag wiring lives in `MohistCliCommands.Issue.CrudWrites.cs`.
- `mo issue list --parent <number>` filter in `MohistCliCommands.Issue.CrudReads.cs`.
- No new transport; reuses the existing PATCH/POST/GET issue endpoints.

## Risks / Trade-offs

- **[Cross-aggregate race on attach vs. parent start]** Two aggregates means the grain's read-then-commit is not atomic across them. Worst case: B starts its workflow in the window between A's grain reading B as Backlog and A committing `AssignParent(B)`, leaving A parented to a running B. -> **Mitigation:** the *harmful* direction is already closed — a parent cannot start once it has a child (Decision 4 reads children at start time). The residual race (A attaches to an already-started B) is benign within #418's scope: A still runs as a normal issue with its own workflow; no composite state is derived yet. It will be tightened by the composite-advancement issue's parent state machine. Documented as a known convergence gap, consistent with `issue-coordination.md`'s "正确性不依赖查询与命令之间的原子性" stance.
- **[`HasWorkflowStarted` as the attach guard is durable]** A Reopened issue (Backlog status, but `HasWorkflowStarted == true`) cannot become a child. -> **Trade-off:** this is intentional ("未启动"), matching the accepted design, but may surprise users who expect a reopened issue to behave as fresh. Surfaced via a typed rejection message; no mitigation needed beyond clear messaging.
- **[Epic link path gains a new rejection]** `mo epic link` previously succeeded for any non-closed-epic link; it now rejects sub-issues. -> **Mitigation:** typed 409 code (`issue_is_sub_issue`) so callers can distinguish it from `EPIC_CLOSED_CANNOT_LINK`; the rejection is only reachable when the target is already a child, which requires the new `--parent` flow, so existing automation is unaffected unless it mixes the two.
- **[Read-time children summary cost]** Deriving "is a parent / children count" by query on every detail/list read adds a lookup. -> **Mitigation:** the query is a cheap indexed filter on `ParentIssueNumber` (same cost as the existing `EpicNumber` membership queries Epic already runs). No denormalization introduced.

## Migration Plan

1. **Schema (additive only):** add `ParentIssueNumber` column to `IssueRow`; migration backfills all rows to null. No existing row carries a parent, so no data transformation.
2. **Deploy order:** server first (aggregate, grain, routes, migration), then CLI (flags), then Web (read rendering). The new column and DTO fields are optional/nullable, so a mixed-version rollout is safe: older clients never send `--parent`; older servers ignore the unknown field on PATCH (the `Fields` set simply won't contain it).
3. **No backfill of relationships:** there are no legacy parent-child facts to import; the column starts empty.
4. **Rollback:** revert the deploy; the nullable column and the new (unused) blocker variant are harmless to leave in place. Dropping the column is optional and unnecessary for rollback safety.

## Open Questions

1. **Start-blocker code name.** Should the blocker code be `is_parent` (the issue's role) or `has_children` (the condition)? Leaning `is_parent` for symmetry with `draft`. Decidable at implementation; both satisfy the spec's "distinct code" requirement.
2. **Re-parenting semantics under the three-state PATCH.** `mo issue update 7 --parent 42` when 7 is already a child of 5: the spec allows "replacing any previous parent". Should this require `--parent none` first, or is a direct move atomic? The aggregate can do it atomically (single `ChangeParent` transaction), and direct move is the more ergonomic choice — but it needs the same single-level and eligibility guards against the *new* parent. Confirm direct move is the intended UX during implementation.
3. **Children summary depth.** Detail read needs "at minimum the fact that children exist and a count". Does the parent's detail also need child numbers/titles (useful for navigation), or is count + a separate `list --parent` call enough for #418? Leaning count-only for the detail read; full list via the filter.
