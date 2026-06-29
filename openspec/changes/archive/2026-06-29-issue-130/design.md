## Context

#129 shipped generic `agent-launch` `AgentSession`s: an Agent profile can be
launched outside any workflow, and the launcher stamps
`mohist.io/source-kind = agent-launch` plus `mohist.io/agent-id`,
`mohist.io/agent-name`, and the optional `mohist.io/agent-launch/*` context-ref
labels onto the session metadata (`GenericAgentSessionMetadata.cs:70`). Those
sessions are then reachable by session id for followup/cancel
(`AgentSessionFollowupRoutes.cs`, `AgentSessionCancelRoutes.cs`).

The read layer, however, has not caught up:

- **Labels are not queryable.** `AgentSessionQuery.QueryRowsByLabels`
  (`AgentSessionQuery.cs:105`) knows only the 8 workflow-shaped keys; every
  agent-launch key falls into the `_ => query.Where(_ => false)` branch, so a
  by-agent or by-context-ref filter matches nothing. The 6 agent-launch labels
  are also not projected as stored computed columns on `AgentSessionRow`
  (`AgentSessionRow.cs:16`), so even adding switch cases would yield
  un-indexed full scans.
- **Activity mis-attributes generic sessions.** `ToActivityCard`
  (`AgentSessionQuerier.cs:627`) always synthesizes
  `issue_{projectId}_{issueNumber}`; a generic session with no issue ref
  resolves to `issue_{projectId}_0`, appearing as a phantom issue card with no
  agent identity.
- **Active-agents readout excludes generic sessions.**
  `WorkflowActivityQuerier.ListActiveAgentsAsync`
  (`WorkflowActivityQuerier.cs:44`) skips any record whose `workflowRunId` or
  `workId` is blank — exactly the generic-session shape.
- **Generic summary is workflow-shaped.** `GetGenericSessionMetadataAsync`
  (`AgentSessionQuerier.cs:293`) reuses `BuildSessionMetadataDtoAsync`, which
  stuffs the `sessionId` into `SessionName` and exposes no agent identity or
  context refs. The spec forbids fabricating workflow-only fields.
- **No agent-scoped list and no issue/epic association reads.** The only
  generic list axis is `GET .../agent/sessions` (project-wide); there is no
  way to ask "this agent's sessions" or "sessions referencing this issue".

Stakeholders: server (sessions query, activity, API), CLI (`mo agent session`),
and the downstream Web workbench (#132) which needs a stable read contract.

## Goals / Non-Goals

**Goals:**
- Make `agent-id`, `agent-name`, and the four `agent-launch/*` context-ref
  labels first-class, indexed query keys on the generic-session read model.
- Provide an agent-scoped session list with status filtering
  (`running` / `completed` / `failed` / `stopped`), ordered by recency.
- Enrich the generic-session summary with agent identity, timing, resolved
  model, usage, failure category, tool counts, and recorded context refs —
  without fabricating workflow-only fields.
- Attribute generic sessions by agent in the activity feed and the
  active-agents readout; stop synthesizing `issue_{projectId}_0`.
- Surface issue/epic context references as lightweight, read-only session
  associations that link back to the session.
- Expose stable HTTP contracts and CLI verbs for #132 and CLI consumption.

**Non-Goals:**
- No execution scheduling (done in #129), no Agent CRUD (done in #128), no
  Web components (#132).
- No new `AgentTask` read model; generic sessions reuse the existing
  `AgentSession` transcript and state model.
- No workflow-board column integration, no issue/epic/project supervisor
  view, no approval authority.
- No write-side scope/mount/supervisor lifecycle for context refs — the
  association is a read-only link.

## Decisions

### D1 — Index the agent-launch labels as stored computed columns

Add 6 stored computed columns to `AgentSessionRow` and the
`MohistDbContext.AgentSessions` entity, mirroring the existing
`json_extract("State", '$.metadata.labels."…"')` pattern
(`MohistDbContext.cs:128-143`):

- `LabelAgentId` ← `mohist.io/agent-id`
- `LabelAgentName` ← `mohist.io/agent-name`
- `LabelAgentLaunchIssueNumber` ← `mohist.io/agent-launch/issue-number`
- `LabelAgentLaunchEpicNumber` ← `mohist.io/agent-launch/epic-number`
- `LabelAgentLaunchRepository` ← `mohist.io/agent-launch/repository`
- `LabelAgentLaunchWorkspacePath` ← `mohist.io/agent-launch/workspace-path`

Add a composite index on `(LabelAgentId, LabelProjectId, CreatedAt)` for the
agent-scoped recency list, and single-column indexes on
`LabelAgentLaunchIssueNumber` / `LabelAgentLaunchEpicNumber` for the
association reads. Extend the `QueryRowsByLabels` switch
(`AgentSessionQuery.cs:105`) with 6 cases mapping these keys to their columns;
the existing workflow-shaped keys stay byte-identical.

- *Alternative A:* generic `json_each` label scan. Rejected — no index, O(n)
  over all sessions, and inconsistent with the established computed-column
  pattern that already serves the 8 workflow keys.
- *Alternative B:* a separate `AgentSessionLabels` index table (the pre-`20260620`
  shape). Rejected — the project deliberately moved off it to stored computed
  columns; reintroducing it would fork the indexing strategy.
- Key constants are owned by `GenericAgentSessionMetadata` already; the new
  switch cases and the computed-column SQL both reference those constants to
  prevent drift.

### D2 — Status filter resolved in-memory over a bounded, indexed result set

Extend the agent-scoped list to accept a `status` set covering at least
`running` / `completed` / `failed` / `stopped`. The DB query does the heavy
lifting on the indexed columns (project + agent + recency with a sane limit);
terminal status (`completed` / `failed` / `stopped`) is then resolved per
session by reusing `LoadTerminalFactsAsync` (`AgentSessionQuerier.cs:826`),
and the requested status set is applied as an in-memory filter.
`running` maps to "opened and no terminal fact and `AgentSessionId` present".

- *Alternative A:* a stored `TerminalStatus` column written from the grain on
  close. Cleaner long-term, but couples the write path to a new derived field
  and adds backfill complexity for in-flight sessions. Deferred — revisit only
  if the workbench shows thousands of sessions per agent (the list is capped
  and recency-ordered, so the in-memory pass is cheap).
- *Alternative B:* push the filter into SQL via a join on the transcript
  parts. Rejected — it would duplicate `LoadTerminalFactsAsync` logic in raw
  SQL and complicate the single-statement query shape.

### D3 — Agent-scoped list endpoint and shared `agentRef` resolution

Add `GET /api/projects/{projectRef}/agents/{agentRef}/sessions?status=&limit=`
returning generic `agent-launch` sessions for the resolved Agent profile,
ordered by recency. `{agentRef}` (name or `agent_*` id) is resolved with the
same `agent_*` → id-else-name rule already in `AgentSessionLaunchRoutes.cs:115`.
Extract that helper into a shared static (e.g. `AgentRefResolver`) so the
list endpoint and the CLI share one implementation; the launch route keeps
its in-file copy as a thin caller.

The query composes labels `(project-id, agent-id, source-kind=agent-launch)`,
so it cannot leak workflow sessions or another agent's sessions. Unknown
`agentRef` → `404`. Distinct from `GET .../agent/sessions` (project-wide) and
the workflow `.../issues/{n}/sessions` list.

- *Alternative:* overload the existing `GET .../agent/sessions` with an
  optional `agent` parameter. Rejected — the spec mandates a distinct,
  agent-scoped route shape and explicit `agentRef` path segment for #132.

### D4 — Dedicated `GenericAgentSessionSummaryDto` (no fabricated workflow fields)

Introduce a new summary DTO carrying agent identity (`agentId`, `agentName`),
`status`, `created`/`lastActivity`, resolved model, usage, failure category,
tool call/error counts, and an optional `contextRefs` envelope
(`issueNumber`, `epicNumber`, `repository`, `workspacePath`). Workflow-only
fields (`workflowRunId`, `sessionName`, `workId`, `workType`, `stage`) are
absent by construction, satisfying "absent rather than null".

The route path stays `GET /api/projects/{projectRef}/agent-sessions/{sessionId}`
(already registered in `AgentSessionFollowupRoutes.cs:31`) — only the response
shape changes, additively. A new `GetGenericSessionSummaryAsync` builds the
DTO from `FindGenericSessionAsync` + `LoadTranscriptAsync` (for counts and
failure category), reusing the existing `TranscriptEventSummaryProjector` and
`AgentSessionJsonHelper`. The legacy `GetGenericSessionMetadataAsync` (used by
#129 followup) is retired in favor of the richer method, since the route
contract is the same and its only consumers ignore the body shape.

- *Alternative:* add agent fields to the existing `AgentSessionMetadataDto`.
  Rejected — that DTO's `SessionName` is structurally required and would have
  to be nulled, contradicting "absent rather than null" and risking silent
  fabrication in other callers.

### D5 — Activity attribution by agent, not synthetic issue

In `ToActivityCard` (`AgentSessionQuerier.cs:627`), branch on
`source-kind = agent-launch`. For generic sessions, synthesize an
agent-attributed identity (`agent_{agentId}`) and populate new optional
`AgentId` / `AgentName` fields on `ActivityCardDto` instead of
`issue_{projectId}_0`. Generic sessions that carry an issue ref still surface
the issue number (so the feed can link them), but the card's primary
attribution is the Agent profile. Workflow cards take the existing branch
unchanged — the new fields stay `null` for them.

`ActivityCardDto` gains two nullable fields; no existing field is removed or
reordered, so the Web/CLI consumers see only additive change.

- *Alternative:* a parallel `GenericActivityCardDto` and a union feed.
  Rejected — the activity feed is a single list contract; a union would force
  every consumer to fork rendering.

### D6 — Active-agents readout includes generic agent-launch sessions

In `WorkflowActivityQuerier.ListActiveAgentsAsync`
(`WorkflowActivityQuerier.cs:44`), drop the blanket
`workflowRunId`/`workId` blank-skip. For a generic active session, emit an
`ActiveAgentDto` attributed by agent identity with a progress entry derived
from session state (last-activity timestamp + a degenerate
`TaskProgress(completed=0, total=0)` or `null`), not from a workflow run.
Extend `ActiveAgentDto` with optional `AgentId`/`AgentName`; workflow entries
remain byte-identical (the workflow branch keeps its current field values).

- *Alternative:* leave active-agents workflow-only and build a separate
  generic "live agents" endpoint. Rejected — the spec explicitly requires the
  existing readout include generic sessions so the workbench shows one live
  picture.

### D7 — Issue/epic association reads via context-ref labels

Add two read-only endpoints:

- `GET /api/projects/{projectRef}/issues/{number}/agent-sessions`
- `GET /api/projects/{projectRef}/epics/{epicRef}/agent-sessions`

Each queries generic sessions by `(project-id, source-kind=agent-launch,
agent-launch/issue-number | epic-number)` labels (D1's new indexed columns)
and returns a lightweight association list: `sessionId`, `agentId`,
`agentName`, `status`, `createdAt`, and a link back to the summary route.
Empty result → `200 []`. The epic route resolves `{epicRef}` by number-then-id
exactly as the existing `EpicRoutes` inline resolver does. These endpoints
perform no writes and create no scope/mount/supervisor — the association is
purely a read derived from labels the launcher already stamps.

- *Alternative:* persist an explicit `IssueAgentSession` link table written at
  launch. Rejected — it would duplicate data already in labels and add a
  write-path obligation for a read-only concern.

### D8 — CLI `mo agent session list | show | transcript`

Extend the existing `session` parent in `MohistCliCommands.Agent.cs`
(`BuildSession`) with three subcommands mirroring `IssueCommands.BuildSession*`:

- `list <agent> [--status]` — resolves `agentRef` client-side via the existing
  CLI `ResolveAgentAsync`, then `GET .../agents/{agentId}/sessions`.
- `show <sessionId>` — `GET .../agent-sessions/{sessionId}` (the enriched D4
  payload).
- `transcript <sessionId>` — `GET .../agent-sessions/{sessionId}/transcript`,
  table-mode renders a summary (part count, first/last activity), JSON-mode
  prints the raw payload — identical shape to `mo issue session transcript`.

Add `TableShape` entries and `TableRenderer` rows for the three payloads. The
existing `mo agent session launch | followup | cancel` verbs are untouched.

## Risks / Trade-offs

- **[In-memory status filter does not scale to huge per-agent session
  counts]** → Mitigation: the list is recency-ordered and capped (default
  limit); the indexed `(agent, project, createdAt)` composite does the
  filtering. Revisit with a stored `TerminalStatus` column (D2 alt-A) only if
  real workbench data justifies it.
- **[Enriching the generic summary response may surprise #129 callers]** →
  Mitigation: the route path is unchanged; fields are additive. The only
  pre-existing consumers (followup/cancel) do not read the summary body.
  Retiring `GetGenericSessionMetadataAsync` is safe because its single caller
  is the route we are reshaping.
- **[Widening `ActivityCardDto` / `ActiveAgentDto` touches the workflow card
  contract]** → Mitigation: new fields are nullable and stay `null` for
  workflow cards; no field is removed or reordered. Web (#132) renders
  conditionally.
- **[SQLite stored-computed-column migration requires add-then-AlterColumn
  table rebuild]** → Mitigation: follow the established
  `ReplaceAgentSessionLabelsWithComputedColumns` migration pattern exactly
  (add nullable, then `AlterColumn` to stored computed); validate against the
  in-memory SQLite integration fixture.
- **[Computed-column SQL and switch-case key drift]** → Mitigation: both
  reference the `GenericAgentSessionMetadata` constants; a spec covers the
  "unknown label is rejected, indexed label resolves" invariant.
- **[`agentRef` resolution now shared by launch + list + CLI]** → Mitigation:
  extracted into one `AgentRefResolver` with a single test; divergence risk
  collapses to one implementation.

## Migration Plan

1. **Model + migration (D1):** add the 6 stored computed columns and indexes
   to `AgentSessionRow` + `MohistDbContext`; add a new EF Core migration
   following the add-then-AlterColumn pattern. No data backfill — columns
   derive from existing `State` JSON. Per `AGENTS.md` the project is
   pre-release with no compatibility constraint, but repo convention is
   additive migration files; ship a new migration file rather than editing
   historical ones.
2. **Query layer (D1, D2):** extend the `QueryRowsByLabels` switch; add the
   agent-scoped list method and the in-memory status filter on
   `AgentSessionQuerier`.
3. **Read models (D4, D5, D6):** introduce `GenericAgentSessionSummaryDto`;
   branch `ToActivityCard` on `source-kind`; drop the blank-skip in
   `ListActiveAgentsAsync` and add agent-attributed entries.
4. **API (D3, D7):** register the agent-scoped list route and the two
   association-read routes; extract `AgentRefResolver`.
5. **CLI (D8):** add the three verbs and their table shapes.
6. **Tests:** integration specs via `MohistIntegrationFixture` + direct
   `AgentSessionRow` seeding (the established `AgentUsageTimeseriesApiSpecs`
   pattern); add focused `AgentSessionQuerier` unit specs using the
   `FakeDbContextFactory` on `SqliteConnection(":memory:")` pattern for the
   label/status filtering invariants.

**Rollback:** revert the change. The new computed columns and indexes can be
dropped (Down) with no data loss — all label data remains in the `State`
JSON. The retired `GetGenericSessionMetadataAsync` is restored trivially if
needed. No external system or write-path is affected.

## Open Questions

- **O1:** Default cap for the agent-scoped list — fixed 50, or configurable via
  `?limit=` like the project-wide list? Lean configurable with the same clamp
  (1–200) for consistency.
- **O2:** Should generic sessions that reference an issue also appear in the
  existing workflow `GET .../issues/{n}/sessions` list, or only in the new
  `.../agent-sessions` association endpoint? Lean: association endpoint only —
  the workflow list stays workflow-shaped, avoiding cross-contamination.
- **O3:** For the active-agents progress of a generic session, is a degenerate
  `TaskProgress(0,0)` acceptable, or should the entry carry a session-derived
  notion of progress (e.g. turn count)? Lean degenerate for v1; the workbench
  can enrich later without an API break since the field is already nullable.
