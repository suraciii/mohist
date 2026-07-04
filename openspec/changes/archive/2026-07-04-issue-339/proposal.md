## Why

Phase 1 gave ops tasks a readable execution log, but an agent task's task-log
panel is effectively empty — the agent's progress lives in a separate
AgentSession transcript that never enters the task-log store, so the user opens
the task panel and cannot tell whether the agent started, which model it bound,
or how it ended (success/failure and why) without leaving the panel to hunt down
the transcript. The milestone facts the user needs (bound model, end status,
failure reason) are **already** persisted in the session summary and reachable
from data the Web holds today; the gap is purely that nothing stitches them into
the task view. This issue closes that view-layer gap while preserving Phase 1's
hard boundary: task-log stays the ops execution trace, transcript stays the
agent dialogue trace, and the two are coupled only at render time — never in the
domain.

## What Changes

- Render **agent-task milestone rows** inside the existing task-log panel:
  bound/resolved model (from the session's `model.resolved`), and session end
  status with failure reason (from `session.closed`). These are boundary/summary
  facts, **not** agent dialogue — dialogue stays in the transcript.
- Merge milestone rows into the **same timeline** as Phase 1 ops log lines,
  sorted by time; give them a **distinct visual treatment** (marker/icon/color)
  so a session event is unmistakable versus a command-output line.
- Gate milestones to **agent tasks only**, judged by the data the Web already
  trusts — `origin.uses === 'mohist/acp-agent'`, presence of `sessionName`, and
  `classification` — never by workType. Pure ops tasks show no milestone rows.
- Resolve a task's session by joining `task.sessionName` to the **existing**
  workflow-run sessions data (`useWorkflowRunSessions`, already live-patched);
  read `eventSummary.resolvedModel`, `status`, `failureReason`, and the
  started/completed timestamps from that summary. **No new endpoint, no new
  query param, no runner collection, no server change.**
- Compute milestones **transiently at render time** and keep them out of the
  `TaskLogPage` React Query cache, out of `mergeTaskLogDelta`, and out of the
  task-log store — they are a view-layer projection, not first-class task-log
  data.
- Deliver **terminal-state visibility as the acceptance floor**: once the session
  ends, the model + outcome (success/failure + reason) milestones are visible
  from the persisted summary alone, with **no dependency on the Phase 2
  real-time channel**. Live display of the bound model mid-session is an
  enhancement that rides the existing sessions live-patch, not a hard
  acceptance item.
- Keep Phase 3a search/filter/download working: milestone rows are sparse
  semantic anchors; the keyword filter applies to them, source-chip filtering
  stays an ops-line concern.
- Add unit coverage for the milestone merge/gating and an a11y case for the new
  row variant.

Non-goals: persisting session events into task-log (would couple the domain);
surfacing agent dialogue in the task panel (transcript's job); multi-end parity
(CLI etc.); milestones for ops tasks (no session concept); changing the
transcript store, its queries, or its channel.

## Capabilities

- `agent-task-milestone-stitching`: The frontend data contract and boundary
  invariants for deriving milestone facts. Resolves a task's agent session by
  joining `task.sessionName` to the existing workflow-run sessions data;
  derives the milestone set (bound/resolved model, end status, failure reason,
  relevant timestamps) from the session summary; identifies agent tasks via
  `origin.uses` / `sessionName` / `classification`. Enforces the invariants:
  milestones are a transient view-layer projection — never written to the
  task-log store, never merged into the log cache/delta, no runner/server/domain
  coupling, no new endpoint. Terminal-state facts must be obtainable from the
  persisted summary without the real-time channel.
- `task-log-viewer`: Extends the existing Phase 1/3a capability to render
  agent-task milestone rows merged into the ops log timeline, sorted by time
  alongside ops lines, with a distinct visual marker separating session events
  from command output. Covers interaction with Phase 3a controls (keyword
  search applies; source chips remain ops-only), the terminal-state-visible
  floor, and a11y for the new row variant. The `TaskLogLine` data model and
  Phase 1/2/3a acquisition (REST snapshot, SignalR delta, truncation) are
  unchanged.

## Impact

- **Web (React)** — sole code surface:
  - `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx`: the
    timeline→`StageTaskState` map currently drops `sessionName`/`origin`/
    `classification`; preserve them (mirror `WorkflowView`'s
    `workflowTimelineToStageStateMap`) and forward `sessionName` into
    `TaskLogPanel`.
  - `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx`: accept the
    session linkage, call `useWorkflowRunSessions(workflowRunId)`, compute
    milestones transiently, and render a merged time-sorted timeline with a
    distinct milestone row variant. Milestones must not flow through
    `mergeTaskLogDelta` or the `TaskLogPage` cache.
  - New helper: an agent-task predicate over `origin.uses`/`sessionName`/
    `classification` (none exists today; `mohist/acp-agent` appears only in
    fixtures).
  - Data source is the existing `entities/coder-session` hook
    (`useWorkflowRunSessions`) and its `WorkflowRunSession` summary
    (`eventSummary.resolvedModel`, `status`, `failureReason`); no new query.
- **Tests (web)**:
  - Extend `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx`
    with a Phase 3b block: mock `useWorkflowRunSessions` (mirroring the existing
    `getIssueWorkflowTaskLog` mock pattern), assert milestone rows interleave by
    time, are visually marked, appear only for agent tasks, and that
    terminal-state facts render without the real-time channel.
  - Add an a11y case under `packages/web/tests/a11y/task-log-a11y.test.tsx`
    covering the milestone row variant (axe + tab order).
- **No changes** to: server (C#), runner (TypeScript), the task-log REST
  endpoint, the SignalR delta channel, the `TaskLogLine`/`TaskLogPage` wire
  types, the `WorkflowRun`/`WorkResult` domain, the task-log store, or the
  transcript store/queries. Phase 1/2/3a behavior must not regress.
