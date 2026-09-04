# Self-Review — issue-558 (Agent 历史与 Session 时间线：解释执行结果)

First review: full sweep across coverage, correctness, codebase consistency, and task
breakdown. Every claim below was checked against the live worktree, not just the artifacts.

## Verdict

PASS — no must-fix finding. The plan is ready to build. Reservations are recorded as
observations and do not affect this verdict.

## Must-Fix Findings

None.

## Issue Acceptance Coverage

**Checked, no issue.** Each acceptance criterion in the issue is addressed by a spec
requirement plus a task with pinned tests:

- *历史不重复、可区分任务/结果/上下文* → `agent-execution-history` "One history record
  per execution" (Job id identity; Turn `(SessionId, TurnId)`; per-response suppression of
  launch Turns whose Job record is in the same response; no record in two sections) — T-001
  dedup tests, T-003 single-list page tests.
- *状态、结果摘要、起止时间、耗时、模型、成本* → "History records carry task, outcome,
  result, context, timing, model, and cost" with absent-when-nonexistent semantics — T-001
  honest-field tests. Job timing comes from `AgentJobRow.SubmittedAt`/`TerminalAt` (both
  verified on the row); model from the same transcript-reduction source the summary reads
  (`TranscriptReductions.LoadEventSummariesAsync`); cost from `AgentUsageSummary.CostAmount/
  CostCurrency` with mandatory `attribution: "session"`.
- *时间线区分输入、回复、关键动作、错误、Compact、Reset、未知* →
  `session-timeline-interpretation` requirement 1 (exactly-one-class classification, honest
  fallback, unknown never failed) — T-004 widget tests.
- *折叠但不隐藏失败/领域动作* → requirement 2 (≥3 same-class collapse; error/domain-action/
  input/message/status/boundary break runs and never collapse) — T-004 collapse tests.
- *历史、Session 页面、导出保留同一执行上下文* → `session-result-export` "The export agrees
  with the history record and the Session page" + timeline "Timeline vocabulary matches the
  history contract" — T-002 same-derivation test, T-004 shared-vocabulary tests. Export is
  issue-required (AC5 mentions 导出结果), so adding it is in scope, not scope creep.
- *从历史进入 Session，刷新后仍理解同一执行* → history "Record links to the anchored
  Session page" (`/sessions/{id}?turn={turnId}`; Job rows anchor `InitialTurnId`, verified
  on `AgentJobRow`) + timeline "Refresh keeps the same understanding" (URL-param anchor, not
  component state) — T-003 link tests, T-004 anchoring tests.

The issue's non-goals are honored: no Input/Turn lifecycle, recovery, or settlement changes;
read-time projection only; no per-Turn cost fabrication (usage is genuinely Session-level —
verified `AgentUsageSummary` sits on `AgentSessionStatusSnapshot`).

## Correctness

**Checked, no issue.** I tried to construct failure cases for each mechanism; each holds
against the actual data model:

- *Dedup is implementable*: `AgentTurnRecord.JobId` is stamped by the Session at input
  acceptance (verified in `AgentSession.Transitions.cs` — launch path stamps `JobId`,
  follow-up stamps `null`), so launch-Turn suppression never depends on inference. Broken
  linkage degrades to honest duplication, and per-response suppression means a status filter
  or limit can never hide an execution entirely.
- *Task summary derivation is backed by facts*: `AgentSessionInputRecord.Text` exists in the
  session state (the same source `AgentSessionObservationMapper` reads), and the fallback
  chain `InitialInputId` → launch Turn `PromptText` (`AgentSessionTranscriptTurnRow`) →
  `AgentJobRow.Title` matches real columns.
- *Status vocabulary is faithful*: `AgentJobStatus` (Pending/Running/Completed/Failed/
  Cancelled/Unknown) and `AgentTurnStatus` (Queued/Executing/Completed/Failed/Unknown/
  Cancelled) both map onto the normalized `pending|running|executing|completed|failed|
  stopped|unknown` vocabulary; the `cancelled→stopped` normalization claim matches the
  documented behavior of `ListAgentSessionsAsync`; `mo agent job view` indeed keeps its own
  wire vocabulary (`ToStatusString` returns `cancelled`).
- *Presentation defect is real and the fix is right*: the current
  `AgentDetailPage.tsx` verifiably groups `activity === 'unknown'` into a "Failed" section
  with a red `XCircleIcon`, and duplicates sessions across Ended/Recent. Replacing four
  fixed groups with one list + status-chip filtering over one query is the only layout that
  satisfies the no-duplicate constraint for a mixed Job/Turn record set.
- *Read API composes with existing conventions*: `ProjectResolutionEndpointFilter` +
  `AgentRefResolver` 404 + 400-on-invalid-status are all existing patterns
  (`AgentSessionListRoutes`, `AgentJobReadRoutes`); the `[1,200]`/default-50 clamp matches
  both sibling lists; `/agents/{agentRef}/history` does not collide with any existing
  route segment; `/sessions/{sessionId}/export` composes with the existing
  `/sessions/{sessionId}` and `/sessions/{sessionId}/transcript` group.
- *Export standalone-understandable and read-only*: identity + context + result + timing +
  attributed cost in one GET with no generation timestamp; no state mutation paths touched.
- *Refresh anchoring*: `?turn=` is genuinely new (no search-param turn handling exists in
  `pages/session`), and it composes with the existing raw/interpreted toggle, which already
  preserves scroll by `data-timeline-source-id` (verified in `SessionDetailShell`).
- *Vocabulary consistency is mechanical*: the timeline renders turn outcomes from the same
  `AgentTurnObservation` facts the projection reads, and the export reuses the projection —
  same-derivation is testable (T-002 pins it).

## Current-Code Consistency

**Checked, no issue.** Every artifact the plan names exists where the plan says:

- Server: `packages/server/src/Mohist.Server/Sessions` (read models, queriers),
  `AgentJobQuerier.ListByAgentAsync` with the `LaunchVisibility` gate, the label-indexed
  `AgentSessionQuery.ListByLabelsAsync` (which also filters session
  `LaunchVisibility`), `AgentSessionObservationMapper`, `UnifiedSessionContextRefsDto`
  envelope, `WhenWritingNull` JSON idiom (`Infrastructure/JSON.cs`), sibling routes.
- Web: `entities/agent/api` (queries live here today), `pages/agent-detail`,
  `pages/session`, `widgets/session-transcript`; `useAgentSessions`/`SessionSection` are
  consumed only by `AgentDetailPage`, so deleting them is safe.
- CLI: `MohistCliCommands.Agent.cs` (job list/view as described — lifecycle-only fields
  today), `MohistCliCommands.Session.cs` (`mo session view <session-id>` exists for
  navigation), `ResourceOutputCatalog` + `TableShape` registrations, `docs/cli-reference.md`.
- Docs: `docs/agent-sessions.md` ("Why Unknown Fails Closed" section exists),
  `docs/web-ui.md` (Agent-detail and Session Implementation Gaps sections exist to be
  closed), `design/session-timeline.md`.
- tasks.json/spec formats match the sibling change (issue-589) key-for-key; every
  `tasks[].spec` heading anchor resolves to a real requirement heading in the spec files.

## Task Breakdown

**Checked, no issue.** T-001 (server projection + history route) → T-002 (export route,
reuses T-001) and T-003 (Web history page, consumes T-001) → T-004 (session page, consumes
T-002 + T-003's shared vocabulary) → T-005 (CLI, consumes T-001 + T-002). Ordering is
dependency-correct, each task carries focused acceptance criteria that pin end-state
behavior (not implementation), named outputs, doc assignments, and verification commands
matching the repo gate (`npm run test:fast` / `npm run verify`). The regression pin on the
existing jobs/sessions list endpoints is present in T-001.

## Observations

Non-blocking; none affects the verdict.

1. **Stale premise in T-004/D8 about the current timeline state.** The four "remaining
   #427 derivation gaps" the design lists (input items in the timeline, `mo`-command
   domain-action recognition, ≥3 collapse with breakers, Compact/Reset boundaries, unknown
   presentation) are already substantially implemented in
   `packages/web/src/entities/session/model/timeline/` (`derive.ts`, `group.ts`,
   `domain-actions.ts` with issue/run reference extraction) and rendered via
   `widgets/session-transcript` (`SessionTranscriptLayout` → `TimelineItemList`); the raw
   view also exists (`RawTimelineView.tsx`). `design/session-timeline.md`'s Status section
   and `docs/web-ui.md`'s "There is no raw event view" gap text are outdated relative to
   the code. The task remains correct because its acceptance criteria pin end-state
   behavior (audit-and-extend rather than build-fresh satisfies them equally), but the
   implementer should treat T-004 as a gap audit against current code plus the genuinely
   new deliverables (shared vocabulary module, `?turn=` anchoring, export action) — and
   must reconcile the stale Status/gap text when updating the docs.
2. **Turn-record timing source is implied, not named.** D3 says `startedAt`/`endedAt` are
   authoritative but only `RecordedAt`/`UpdatedAt` exist per Turn (`UpdatedAt` is bumped on
   every transition, including terminal — verified in `MarkTurnExecuting`/`MarkTurnTerminal`).
   Mapping `RecordedAt`→startedAt and terminal `UpdatedAt`→endedAt (absent while
   nonterminal) satisfies AC2; the plan should state it during implementation to avoid a
   fabricated-looking duration.
3. **Name proximity**: an `AgentExecutionHistory` record already exists in
   `Agent/Services/AgentJobQuerier.cs` (used by `GetLatestExecutionAsync` for availability).
   The new `AgentExecutionHistoryQuerier`/`AgentExecutionHistoryItemDto` do not collide, but
   the similar names in nearby namespaces invite confusion; consider renaming the old record
   or keeping the new DTO names distinct in tests.
4. **Transient inert link parameter**: T-003 ships `?turn=` links before T-004 teaches the
   Session page the anchor; between the two tasks the parameter is harmless but inert.
   Acceptable given T-004 lands in the same change.
5. **Older Job rows with null `AgentSessionId`/`InitialTurnId`**: D6 covers the purged/missing
   session case for fields, but the anchor target for a Job record with null `InitialTurnId`
   should degrade to plain `/sessions/{id}`; minor, decide during implementation.
6. **The design's own open questions** (include `agent-connection` sessions in Turn
   records; server-side bounding of `output` in the list; whether client pagination beyond
   the limit cap is needed) each have a stated lean and none blocks an acceptance
   criterion; resolving them during implementation is fine.
7. **Don't copy the sibling 400 message verbatim**: `AgentJobReadRoutes`' invalid-status
   message omits `cancelled` even though `Enum.TryParse` accepts it; the new history route's
   message should list the actual accepted vocabulary (where `cancelled` maps to `stopped`,
   not rejected).
8. **File changes (文件变化)** from the issue's Product Shape are not named as a timeline
   class in the new spec, but they are covered by the existing `file-edit` items (with
   `changedFiles` and diffs) that T-004 preserves, and the binding acceptance criteria do
   not list them separately.
9. This is a static planning review: no implementation tests were run; task acceptance
   criteria are the build-time evidence.

<promise>PASS</promise>
