## Context

Phase 1 gave ops tasks a readable execution log; Phase 3a added search/source-chip/download on top. An **agent task's** task-log panel is still effectively empty: the agent's progress lives in a separate `AgentSession` transcript that never enters the task-log store, so the user opening the panel cannot tell whether the agent started, which model it bound, or how it ended without leaving the panel to find the transcript. The milestone facts the user needs — bound/resolved model, end status, failure reason — are **already** persisted in the `WorkflowRunSession` summary and reachable from a hook the Web already uses (`useWorkflowRunSessions`); the gap is purely view-layer stitching. This change closes that gap while preserving Phase 1's hard boundary: task-log stays the ops execution trace, transcript stays the agent dialogue trace, and the two are coupled only at render time — never in the domain.

Verified current state (code):

- **`TaskProgressPanel` drops the session linkage.** Its timeline→`StageTaskState[]` projection (`packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx:230-244`) reconstructs only `origin: { source: 'runtime', uses: task.uses }` and omits `sessionName` and `classification`. The sibling mapper `workflowTimelineToStageStateMap` in `WorkflowView.tsx:148-166` **does** retain all three (`sessionName` at `:152`, `classification` at `:165`). So the field retention has a proven mirror to copy.
- **`TaskLogPanel` already holds the join key.** Props (`TaskLogPanel.tsx:14-19`) are `{ issueNumber, taskId, workflowRunId?, taskStatus? }`; `workflowRunId` is forwarded from `TaskProgressPanel.tsx:172-174`. The panel fetches the REST snapshot via `useIssueWorkflowTaskLog` (`:103-109`) and live-appends SignalR deltas via `mergeTaskLogDelta` (`:71-98`, sole call site `:129`). Source set and the composed search+source filter live at `:139-152`; the "no execution log captured" empty state at `:274-280`; ops-line render at `:296-306` (`<HH:MM:SS.mmm> [source] text`).
- **The session summary already carries every milestone field.** `WorkflowRunSession` (`packages/web/src/entities/coder-session/model/types.ts:149-170`) exposes `status` (`:157`), `model` (`:159`), `createdAt` (`:162`), `startedAt` (`:163`), `completedAt` (`:164`), `failureReason` (`:166`), and `eventSummary.resolvedModel` (`types.ts:50`). `useWorkflowRunSessions(workflowRunId)` (`useWorkflowRunSessions.ts:14`) returns `{ sessions, isLoading }`, backed by a React Query keyed `['workflow-runs', workflowRunId, 'sessions']` (`:17-22`, `staleTime: 30s`) on `getWorkflowRunSessions` (`api/client.ts:14-15`, `GET /workflow-runs/{id}/sessions`) and **live-patched via the `onAgentEvent` DOM bus** (`:40-144`) — not SignalR. The same query is already warm because `WorkflowView` consumes it on the same page.
- **The wire types are small and must stay untouched.** `TaskLogLine { seq, timestamp, source, text }` / `TaskLogPage { lines, nextCursor, truncated }` (`entities/issue/model/task-log.ts:1-12`); the SignalR envelope `TaskLogDeltaEnvelopeWire` (`shared/api/events-hub.ts:21-29`).
- **No agent-task predicate exists.** `mohist/acp-agent` appears only in fixtures (`WorkflowArtifacts.test.tsx:92,118`). The closest existing pattern is `isDeliveryFailureTask` (`TaskProgressPanel.tsx:181-200`), which reads `task.origin?.uses` and mirrors the placement a new `isAcpAgentTask` helper would take.
- **Test scaffolding to mirror.** Widget unit test mocks the REST client via `vi.mock('../../../entities/issue/api/client', …)` with `mockImplementation` per test (`TaskLogPanel.test.tsx:21-29,151-164`). The hook's own test mocks `getWorkflowRunSessions` + `onAgentEvent` (`useWorkflowRunSessions.test.tsx:9-11,15-27`). A11y lives at `packages/web/tests/a11y/task-log-a11y.test.tsx`, renders via `QueryClientProvider + ProjectProvider`, and runs a **scoped** axe `runOnly` rule list (`:163-189`) plus a real `user.tab()` walk (`:249-271`).

Constraints / stakeholders: per `design/architecture.md` the Web is a view/observation surface — authoritative state lives in the Server; events are for observation, not control. Per `design/conventions.md` `sessionName` is part of the `AgentSession` ResourceKey (`/projects/{projectId}/workflow-runs/{workflowRunId}/sessions/{sessionName}`), so joining on `sessionName` is the canonical linkage; this view is Tier-1 (read-only ops dashboard) so **no CLI, no skill entry, no runner/server contract**. Per `design/testing.md` unit tests are colocated `*.test.tsx`, a11y lives under top-level `tests/a11y/` and runs in `npm run test:a11y` (not default `npm test`); no real time, no real network — all fakes via `vi.mock`.

## Goals / Non-Goals

**Goals:**
- Render agent-task **milestone rows** (bound/resolved model; session end status with failure reason) inside the existing task-log timeline, merged with Phase 1 ops lines and sorted by time, with a distinct visual marker.
- Resolve a task's session by **joining `task.sessionName` to the existing `useWorkflowRunSessions` data** — no new endpoint, no new query param, no new wire type, no runner/server change.
- Keep milestones a **transient view-layer projection**: never written to the task-log store, never in the `TaskLogPage` cache, never produced by `mergeTaskLogDelta`. The `TaskLogLine`/`TaskLogPage`/`TaskLogDeltaEnvelopeWire` shapes stay byte-identical.
- Gate milestones to **agent tasks only**, judged by `origin.uses === 'mohist/acp-agent'` + non-empty `sessionName` + `classification` — never by workType. Pure ops tasks render zero milestone rows.
- Deliver **terminal-state visibility as the acceptance floor**: a finished session's model + outcome milestones render from the persisted summary alone, with no dependency on the Phase 2 real-time channel. Mid-session live model display rides the existing sessions live-patch as a free enhancement.
- Preserve Phase 3a semantics on the merged timeline: keyword search applies to milestone rows; source-chip filtering and the source-chip set remain ops-only; download reflects the filtered view.
- Fix the `TaskProgressPanel` field-drop: retain `sessionName`, `origin.uses`, `classification` (mirroring `WorkflowView`), forwarding `sessionName` into `TaskLogPanel`.

**Non-Goals** (per proposal/specs):
- No persistence of session events into the task-log store; no runner collection; no server/endpoint/wire change.
- No agent dialogue in the task panel (transcript's job); no multi-end parity (CLI etc.); no milestones for ops tasks.
- No change to transcript store/queries/channel, to `mergeTaskLogDelta`, to the SignalR delta path, or to Phase 1/2/3a acquisition.

## Decisions

### D1. Resolve the session inside `TaskLogPanel` via the existing `useWorkflowRunSessions` hook.

`TaskLogPanel` already receives `workflowRunId` and will receive `sessionName` (D6); it calls `useWorkflowRunSessions(workflowRunId)` directly and selects the session whose `sessionName` matches. The hook is the established acquisition path and its query key `['workflow-runs', workflowRunId, 'sessions']` is already warm on this page (consumed by `WorkflowView`), so each expanded panel adds **zero network cost** — React Query dedupes by key. Hooks cannot be conditional, so the call is unconditional; the result is simply ignored for non-agent tasks (D3).

Alternatives considered:
- **Lift the call into `TaskProgressPanel` / `TaskItem` and thread the resolved session down as a prop.** Rejected: it forces the parent to understand milestone rendering, adds prop plumbing through `TaskItem`, and still results in N hook calls (one per expanded panel) — strictly more coupling for no dedupe win (the query is keyed by `workflowRunId` either way).
- **A single shared call in `TaskProgressPanel` passing all sessions down.** Rejected: it fetches sessions even when no panel is expanded, and the query is already shared via React Query anyway.

### D2. Milestones are a new colocated view-layer type, never a `TaskLogLine`.

Introduce a small colocated `TaskLogMilestone` (in `TaskLogPanel.tsx` or a sibling `milestones.ts`):

```
type TaskLogMilestoneKind = 'model-bound' | 'session-ended'
interface TaskLogMilestone {
  kind: TaskLogMilestoneKind
  timestamp: string            // ISO, comparable to an ops line.timestamp
  label: string                // e.g. "Model bound", "Session ended"
  detail: string               // e.g. the resolved model id, or status + reason
  failed?: boolean             // present only on session-ended when failureReason is non-empty
}
type TimelineRow = TaskLogLine | TaskLogMilestone
```

A discriminated union (`'seq' in row` ⇒ ops line) keeps the render and filter branches exhaustive. `TaskLogLine`/`TaskLogPage`/`TaskLogDeltaEnvelopeWire` are untouched; `mergeTaskLogDelta` never sees a milestone; the `TaskLogPage` cache never stores one. This is the structural enforcement of the "transient view-layer projection" invariant.

Alternatives considered:
- **Reuse `TaskLogLine` with a sentinel `source` (e.g. `'session'`).** Rejected: it smuggles milestones through the cache and the delta merge, directly violating the spec's "milestones bypass the log cache and delta merge" scenario, and would force the source-chip set to special-case the sentinel.
- **A shared milestone type in `entities/.../model`.** Rejected: milestones are a render concern, not a domain read model (`design/architecture.md` modeling boundary). Keep them in the widget.

### D3. Agent-task identification is a new colocated predicate over `origin.uses` / `sessionName` / `classification`.

A pure helper colocated with `TaskLogPanel` (mirroring `isDeliveryFailureTask` at `TaskProgressPanel.tsx:181-200`):

```
isAcpAgentTask({ origin, sessionName, classification }): boolean
  => origin?.uses === 'mohist/acp-agent' && typeof sessionName === 'string' && sessionName.length > 0
```

`classification` is carried as a retained field (D6) and is available to the predicate; the deciding fields are `origin.uses` and a non-empty `sessionName`, matching the spec's identification rule. `workType` is never read (it is not a task-level field). Only `TaskLogPanel` consumes this predicate today; if a second consumer appears, lift to `shared/lib` (where the shared `resolveDeliveryFailureFromOutput` lives). YAGNI keeps it colocated for now.

### D4. Milestone derivation is a pure, unit-tested function of the resolved `WorkflowRunSession`.

```
deriveMilestones(session: WorkflowRunSession | null): TaskLogMilestone[]
```

Rules:
- **model-bound** — emitted iff `const m = session.eventSummary?.resolvedModel ?? session.model` is non-empty. `timestamp = session.startedAt ?? session.createdAt`; `label = 'Model bound'`; `detail = m`. Mid-session, this fires as soon as the live-patched summary carries a resolved model (free enhancement via the existing `onAgentEvent` bus); its absence mid-session is not a failure.
- **session-ended** — emitted iff `session.completedAt` is non-null. `timestamp = session.completedAt`; `label = 'Session ended'`; `detail = status` plus, when failed, the `failureReason`; `failed = failureReason` non-empty (the structured signal — see Risk R3).
- Returns `[]` when the join misses (D5) or when neither anchor is present.

This makes the acceptance floor literal: a finished session always has `completedAt` in the persisted summary, so the session-ended milestone is always derivable **without** the real-time channel.

Alternatives considered:
- **Derive a "session started" milestone.** Rejected: the proposal notes there is no independent session-started event; "start" is expressed by `model-bound` (or by `completedAt`'s absence mid-session). Emitting a redundant anchor adds noise.
- **Treat `status` text as the failure signal.** Rejected: `WorkflowRunSession.status` is a free-form `string` (`types.ts:157`); interpreting it is brittle. `failureReason` is the structured failure marker.

### D5. A missing session join degrades to no milestones — never an error.

When `useWorkflowRunSessions` returns no session whose `sessionName` matches (or the task is not an agent task), `deriveMilestones(null)` returns `[]`. The panel renders exactly as Phase 1/3a did. No exception, no blocked ops timeline. This satisfies the "missing session match degrades gracefully" scenario and keeps the change purely additive.

### D6. `TaskProgressPanel` retains `sessionName`, `origin.uses`, `classification` and forwards them into `TaskLogPanel`.

- Extend the projection at `TaskProgressPanel.tsx:230-244` to add `sessionName: task.sessionName` and `classification: task.classification` (mirroring `WorkflowView.tsx:152,165`). The existing `origin: task.uses ? { source: 'runtime', uses: task.uses } : null` already carries `uses` — no change there.
- Extend `TaskLogPanelProps` (`TaskLogPanel.tsx:14-19`) with optional `sessionName?: string | null`, `origin?: { uses?: string } | null`, `classification?: string | null`. Forward them at the `TaskLogPanel` render site (`TaskProgressPanel.tsx:172-174`).
- `TaskLogPanel` then runs `isAcpAgentTask` locally — the parent stays oblivious to milestone semantics.

Alternatives considered:
- **Pass a single pre-computed `isAgentTask: boolean` from `TaskProgressPanel`.** Rejected: it leaks the predicate into the parent and forecloses future in-panel re-evaluation. Passing the raw fields keeps the predicate colocated with its consumer (D3).

### D7. The merged timeline is sorted by ISO timestamp; ops-only ordering by `seq` is preserved within the ops subsequence.

Today the render path is array-order over a `lines` array that `mergeTaskLogDelta` keeps `seq`-sorted (`TaskLogPanel.tsx:88`). The new merged `TimelineRow[]` is sorted by **lexicographic comparison of ISO-8601 `timestamp`** (which is chronological at second resolution and stable). Because ops lines keep their relative `seq` order and milestones carry their own timestamps, the merge is a stable concatenation + timestamp sort. Second-resolution jitter between the runner clock (ops lines) and the server clock (session timestamps) is accepted (R4).

The filter at `TaskLogPanel.tsx:144-152` is generalized to operate on `TimelineRow`:
- **ops line**: unchanged — gate on `disabledSources.has(line.source)`, then search `${text} ${source}`.
- **milestone**: source-chip filter never applies; only search applies, over a `${label} ${detail}` haystack.
The source-chip set (`TaskLogPanel.tsx:139-142`) stays derived **only** from `lines` (ops) — milestones never contribute a chip and are never hidden by toggling an ops chip.

### D8. Empty-state guards consider the merged timeline; the "no execution log captured" copy stays for the truly-empty case.

The empty-state branch at `TaskLogPanel.tsx:274-280` becomes `lines.length === 0 && milestones.length === 0`. For an agent task with no ops lines but a session summary, the milestones render as the timeline's content and the Phase-1 empty copy is **suppressed** (satisfying the "milestones render even when the agent task has no ops lines" scenario). The `task-log-no-search-match` / `task-log-no-source-match` boundaries (`:284-294`) are recomputed against the merged `filtered` array.

### D9. Visual distinction is carried by a non-color-only marker with an accessible name.

Each milestone `<li>` carries: (a) a dedicated marker element (icon glyph) ops lines do not render, with `aria-label="Session event"`; (b) a distinguishing color class; (c) a human label prefix (`Model bound` / `Session ended`). The meaning is conveyed by text + icon + label, not by color alone (a11y scenario). The ops-line styling (`<HH:MM:SS.mmm> [source] text`) is unchanged.

### D10. Download export reflects the filtered merged view; milestone rows are serialized as `[session] <detail>`.

Because the export contract is "reflect the currently filtered view," milestone rows that survive the filter are exported. Serialization: `<timestamp> [session] <label>: <detail>` (a `[session]` marker mirroring the ops `[source]` convention). This keeps the downloaded file faithful to what the user sees without introducing a new export shape.

## Risks / Trade-offs

- **[N `TaskLogPanel` instances each call `useWorkflowRunSessions` → N live-patch subscriptions]** → Mitigated by D1: React Query dedupes the REST call by key; the `onAgentEvent` DOM-bus subscriptions are cheap, all seed from the same query, and apply the same patches, so per-instance `useState` live state converges. Documented as accepted.
- **[Milestone timestamps on a different clock than ops lines → sub-second misordering]** → Mitigated by D7: sort on ISO timestamp; accept second-resolution jitter. The user-visible effect is bounded (a milestone may sit within ±1s of a sibling ops line) and does not affect either data path.
- **[`WorkflowRunSession.status` is free-form; failure detection could misclassify]** → Mitigated by D4: gate `failed` on the structured `failureReason` non-empty signal, and surface the raw `status` text verbatim in the milestone detail (no interpretation). Unit-tested at both the success and failure arms.
- **[Empty-state semantics shift could regress the "no log captured" affordance for ops tasks]** → Mitigated by D8: the guard becomes `lines.length === 0 && milestones.length === 0`; ops tasks (which produce no milestones per D3/D5) keep the exact Phase-1 copy when they truly have no lines. Unit-tested in both branches.
- **[Latent `new Date().toISOString()` fallback inside `mergeTaskLogDelta` (`TaskLogPanel.tsx:82`) is a real-time use flagged by `design/testing.md`]** → Out of scope for this issue; this change does **not** extend the merge path. Noted here to avoid latent surprise; a future issue can inject `now`.
- **[Live model display mid-session could be mistaken for an acceptance item]** → Mitigated by D4 + the spec: mid-session is explicitly an optional enhancement riding the existing live-patch; the acceptance floor is terminal-state visibility, which is always derivable from the persisted summary.

## Migration Plan

1. **Field retention (D6):** extend `TaskProgressPanel` projection (`TaskProgressPanel.tsx:230-244`) with `sessionName` + `classification`; extend `TaskLogPanelProps` and the render site (`TaskProgressPanel.tsx:172-174`).
2. **Predicate + derivation (D3/D4):** add `isAcpAgentTask` and `deriveMilestones` colocated with `TaskLogPanel`; add the `TaskLogMilestone` / `TimelineRow` types (D2).
3. **Panel wiring (D1/D7/D8/D9/D10):** call `useWorkflowRunSessions` in `TaskLogPanel`; build the merged `TimelineRow[]`; generalize filter + source-chip set; update empty-state guards; render the milestone variant; serialize milestones in the download export.
4. **Tests (web unit):** extend `TaskLogPanel.test.tsx` with a Phase-3b block — mock `getWorkflowRunSessions` (+ `onAgentEvent` if exercising live-patch) mirroring the existing `getIssueWorkflowTaskLog` mock pattern; assert: milestones interleave by time, distinct marker present, agent-task-only gating, terminal-state facts render without any live event, search keeps/hides milestones, source chips stay ops-only and never hide milestones, no-op degradation when the join misses. Add focused unit tests for `isAcpAgentTask` and `deriveMilestones`.
5. **Tests (web a11y):** add a case under `packages/web/tests/a11y/task-log-a11y.test.tsx` covering the milestone row variant — assert the scoped axe `runOnly` rule set passes with milestones present and that the marker is not color-only (exposes `aria-label` / accessible name); extend the `user.tab()` walk if the milestone introduces any interactive element.
6. **Regression guard:** confirm Phase 1/2/3a behaviors — `mergeTaskLogDelta` dedupe/sort/truncation, REST snapshot + SignalR delta live-append, terminal cache invalidation, search/source/download — unchanged via the existing tests.

Verification gates: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web`; `npm run test:a11y -w packages/web` (a11y separate per `design/testing.md`). No server/runner tests are touched.

Rollback: revert the PR. The change is a pure Web additive projection — no schema, wire, endpoint, store, or domain change — so rollback needs no data migration and leaves Phase 1/2/3a behavior intact.

## Open Questions

- **Status wording:** the session-ended milestone surfaces `status` verbatim (e.g. `completed` / `failed`). Lean: **show the raw status text** rather than mapping to a fixed vocabulary, to avoid interpreting the free-form field. Confirm the desired display string (e.g. capitalize, or prefix "Status: ").
- **Marker glyph:** the concrete icon for the session-event marker (D9) is an implementation detail; the contract is "non-color-only, with an accessible name." Lean: **a small filled diamond (`◆`) plus `aria-label="Session event"`**, picked during coding.
- **Failure-reason length in the panel:** `failureReason` can be multi-line/long. Lean: **render it inline but whitespace-pre-wrapped within the milestone's `detail` span** (no truncation in the panel; the transcript remains the source of full detail). Revisit if rendering looks noisy in practice.
- **Should the model milestone also show the requested (`session.model`) when it differs from `eventSummary.resolvedModel`?** Spec says resolved with a `model` fallback. Lean: **no** — show one value (resolved, falling back to `model`) to keep the milestone a single boundary fact.
