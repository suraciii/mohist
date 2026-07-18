# Self-Review — issue-414 (Event Routing Table)

Reviewer role: consistency/correctness review of the plan artifacts against the issue
and the current code. No file other than this one was modified.

Artifacts reviewed: `proposal.md`, `design.md`, `tasks.json`, `specs/routing-rules/spec.md`,
`specs/routing-dispatch/spec.md`, `specs/routing-dry-run/spec.md`. Verified against the
live code under `packages/server/src/Mohist.Server/`.

## Summary

The plan is coherent and well-scoped overall: capability boundaries are clean, the
shared-evaluator decision (D2) is the right structural guarantee, the removal scope is
complete, and every issue acceptance criterion is owned by some task/spec. However,
three problems must be fixed before building — two of them contradict explicit, hard
acceptance criteria of the issue. Verdict: **FAIL**.

## Findings

### F-1 [BLOCKING] Dry-run replay population does not match real-dispatch population; equivalence is over-claimed

- **Where:** `design.md` D7 (`ProjectRecentEventReader` "unions the real per-aggregate
  event tables `IssueEvents`, `WorkflowRunEvents`, `EpicEvents`, `AgentSessionEvents`
  filtered by `projectid`"); `tasks.json` T-004 description and acceptance criteria;
  `specs/routing-dry-run/spec.md` "Conclusions match real dispatch" and "Project-scoped
  recent-event selection".
- **Problem:** Real dispatch subscribes `[Subscription(Type = "*")]` and sees **every**
  CloudEvent the bus publishes that carries a `projectid` (see
  `Events/Subscriptions/AgentSubscriptionDispatchHandler.cs:59,73-88`). The dry-run reader
  covers only the four domain event tables. Two event families that are explicitly part
  of the unified envelope and of this issue's routing scenarios are not covered:
  - **Runner events** (`com.mohist.runner.*`). `design/event-routing.md` lists
    "产线维护 | runner 掉线" as a first-class routing target, and
    `design/event-protocol.md`'s stamping matrix shows `runner.*` carries `projectid`.
    So a rule like `event.type == "com.mohist.runner.disconnected"` **will fire in real
    dispatch** but has **no replayable source** in the dry-run reader. Even if such
    events fall into `WorkflowRunEvents` via the catch-all fallback in
    `Infrastructure/Data/Events/EventStore.cs:110-124` (unmatched source → write as a
    WorkflowRunEvent), they are then dropped by the dry-run's project scoping, which
    joins `WorkflowRunEvents` to `WorkflowRuns` on source == prefix+runId
    (`AgentOps/Services/ProjectEventFeedAssembler.cs:90-93`) — a runner-source row has
    no matching `WorkflowRun`, so it is excluded.
  - **Inbox events** (`inbox.item-persisted`) carry `projectid` per the matrix and are
    likewise not in any of the four tables.
- **Why it matters:** The issue states this twice as a hard requirement:
  "求值语义与真实分发完全一致" and "对同一事件序列，干跑结论与真实分发一致". The plan
  currently both (a) defines a reader that omits dispatchable events and (b) asserts full
  equivalence. An operator dry-running a runner/fallback rule will see "no hits" and
  conclude the table is safe, then get a live fire — the exact failure mode the dry-run
  exists to prevent.
- **Recommendation:** Reconcile in the design + spec. Either (preferred) widen
  `ProjectRecentEventReader` to the true dispatched-event population (whatever real
  dispatch sees with a `projectid`, including runner/inbox events, persisted to a
  queryable store — which may require persisting families that today are only live), or
  explicitly narrow the equivalence claim in `routing-dry-run` to "for events the reader
  can replay" and document precisely which event families are excluded and why. The
  current unqualified equivalence must not survive.

### F-2 [BLOCKING] "AgentJob → event/rule" lookup is specified but no task delivers it

- **Where:** `specs/routing-dispatch/spec.md` "Bidirectional event-rule-AgentJob
  visibility" ("from an AgentJob ... the triggering event id and rule id SHALL be
  retrievable") and the issue's acceptance criterion "从 AgentJob 能查到触发它的事件与规则".
- **Problem:** Trigger labels live on the **AgentSession**, not the AgentJob
  (`Infrastructure/Data/Sessions/AgentSessionRow.cs:36-37`; applied in
  `Agent/Services/AgentLauncher.cs:70-90,157+`). The only existing lookup surface is
  session-scoped: `Sessions/Services/AgentSessionQuery.cs:129-130` filters
  `AgentSessions` by `LabelTriggerEventId` / `LabelTriggerSubscriptionId`. There is no
  AgentJob-keyed query that returns the triggering event/rule. `tasks.json` T-003 only
  *renames* the column and updates that existing session query; no task adds an
  AgentJob→rule (or AgentJob→event) query surface, and T-006 doesn't either.
- **Why it matters:** A spec requirement with no owning implementation task is a gap the
  build cannot satisfy.
- **Recommendation:** Either soften the spec to match reality ("from an AgentSession /
  via the session trigger labels the triggering event and rule are retrievable" — the
  AgentJob is reachable through its session), or add an explicit deliverable (query +
  route/CLI) for AgentJob-keyed lookup. Pick one and make spec ↔ tasks agree.

### F-3 [BLOCKING] `design.md` "single migration" contradicts `tasks.json`'s three migrations

- **Where:** `design.md` Migration Plan ("Single EF Core migration in this change:
  1. DropTable AgentSubscriptions, 2. CreateTable RoutingRules, 3. Rename the AgentSessions
  computed column") vs `tasks.json` outputs: T-001 "EF migration (create RoutingRules)",
  T-003 "session computed-column rename migration", T-006 "drop-AgentSubscriptions EF
  migration".
- **Problem:** Cross-artifact inconsistency. The tasks approach (one migration per task)
  is actually correct given the DAG and the "each task leaves its module usable" rule —
  T-001 needs `RoutingRules` to exist at T-001 completion, and T-006 cannot drop
  `AgentSubscriptions` before the subscription resource is gone. The design's "single
  migration" wording is the wrong one.
- **Recommendation:** Update `design.md` Migration Plan to describe the per-task
  migration sequence (create in T-001 → session column replace in T-003 → drop
  AgentSubscriptions in T-006) and drop the "single migration" claim.

### F-4 [Minor] Computed-column "rename" is actually a replace; label *key* changes

- **Where:** `design.md` D4 / Migration Plan ("rename the AgentSessions stored computed
  column to `LabelTriggerRuleId`"); `tasks.json` T-003.
- **Problem:** This is not a pure rename. The stored computed column is defined by
  `JsonExtractLabel(<key>)` (`Infrastructure/Data/Db/MohistDbContext.cs:180-183`). Because
  the label key changes from `mohist.io/trigger/subscription-id` to
  `mohist.io/trigger/rule-id`, the new column extracts a different JSON path. Under
  SQLite this is add-new-column + drop-old (table rebuild), and historical sessions'
  State JSON still carries the old key, so the new column is null for them (already noted
  in T-006 notes — good).
- **Recommendation:** Phrase D4/Migration as "replace the computed column and its
  extraction key", not "rename", so the implementer doesn't attempt an in-place rename.

### F-5 [Minor] Rendering "present-including-empty vs absent" needs a nuance check

- **Where:** `design.md` D6 and `specs/routing-dispatch/spec.md` "Response prompt
  rendering" (present attribute incl. empty → substitute; absent → verbatim).
- **Problem:** Two small things to confirm. (a) The issue says "无值的占位符原样保留"
  ("placeholders with no value are kept as-is"); the plan interprets this as
  absent → verbatim but present-empty → substitute-with-empty. That is a defensible
  reading but stricter than the issue text; it should be called out explicitly so it
  isn't re-litigated during implementation. (b) `CloudEventEventMatchInput.Has()`
  (`Infrastructure/Events/Matching/CloudEventEventMatchInput.cs:53-63`) reports `Has=false`
  for an **empty** core field (`type`/`source`/`subject`) but `Has=true` for a present
  extension with an empty value. So "present-empty → substitute" behaves differently for
  core fields vs extensions. Unlikely to matter in practice (real envelopes have non-empty
  `type`), but the renderer should resolve presence consistently with whatever the spec
  settles on.
- **Recommendation:** State the chosen interpretation in the design and ensure the
  renderer's presence test matches it for both core fields and extensions.

## Cross-artifact consistency

- Capabilities in `proposal.md` (`routing-rules`, `routing-dispatch`, `routing-dry-run`)
  ↔ three spec dirs ↔ tasks: **consistent** (every capability has a spec and owning tasks).
- Removal scope: proposal BREAKING bullet ↔ `routing-rules`/`routing-dispatch` removal
  requirements ↔ T-006: **consistent**.
- `{{event.*}}` + legacy aliases, first-match/`continue`, idempotency, hit-but-not-
  executable, project-scoped entry: **consistent** across proposal/spec/design/tasks.
- Dense-integer Position + `--before`/`--after` + project-unique name: **consistent**
  across spec/design/tasks.
- Dependency DAG in `tasks.json`: valid, acyclic, every `dependsOn` points to a strictly
  lower-priority task (T-001…T-006).

## Acceptance-criteria coverage

All ten issue acceptance criteria are mapped to specs/tasks **except** the two gaps in
F-1 (dry-run ↔ real-dispatch equivalence for all dispatchable events) and F-2
(AgentJob → event/rule lookup). The other eight are fully covered.

<promise>FAIL</promise>
