# Self Review Report

## Result: PASS

Reviewed `proposal.md`, `design.md`, `tasks.json`, and both specs
(`agent-task-milestone-stitching`, `task-log-viewer`) against issue #339
(acceptance criteria + non-goals) and the four review axes
(alignment / completeness / consistency / feasibility / dependency_completeness).

Verification performed:

- **Alignment** — each of the 9 issue acceptance criteria maps to a "What Changes"
  entry and to a task acceptance criterion:
  - AC1 (model + end status rows) → proposal bullets 1 & 6; task AC #3, #6, #7.
  - AC2 (merged time-sorted timeline) → bullet 2; task AC #6; `task-log-viewer` spec
    "merged into the ops log timeline and sorted by time".
  - AC3 (visual distinction) → bullet 2; task AC #8; spec "distinct visual marker".
  - AC4 (from session summary, no runner/store write) → bullets 4 & 5; task AC #4, #5;
    `agent-task-milestone-stitching` spec "transient view-layer projection".
  - AC5 (agent-only gating via origin.uses/sessionName/classification) → bullet 3;
    task AC #2; spec "identified by origin.uses, sessionName, and classification".
  - AC6 (terminal-state visible without real-time channel) → bullet 6; task AC #7;
    spec "Terminal-state milestone visibility is the acceptance floor".
  - AC7 (stores remain independent, no domain coupling) → bullet 5; task AC #5; spec
    "Milestones bypass the log cache and delta merge" + "No runner or server change".
  - AC8 (no Phase 1/2/3a regression) → bullet 7; task AC #11; spec "do not regress".
  - AC9 (a11y) → bullet 8; task AC #10; spec "milestone row variant is accessible".
  Every issue non-goal is echoed in the proposal's Non-goals and the design's
  Non-Goals section; none is violated.

- **Completeness** — both capabilities declared in the proposal have a matching spec
  directory; both spec files are referenced by task `T-001` (`spec` field). Edge cases
  covered: missing session join (degrades to no milestones, D5 + spec scenario), agent
  task with no ops lines (milestones render, empty-state suppressed, D8 + spec),
  failure path (`failureReason` non-empty ⇒ `failed` flag, D4), missing resolvedModel
  (falls back to `session.model`, D4), mid-session live display (explicitly an
  enhancement, not acceptance, D4 + spec).

- **Consistency** — design decisions D1–D10 each trace to a spec requirement/scenario
  (D1↔session join, D2↔bypass cache/delta, D3↔identification rule, D4↔milestone
  derivation, D5↔graceful degradation, D6↔TaskProgressPanel field retention, D7↔time
  sort + search/source semantics, D8↔empty-state shift, D9↔visual marker, D10↔download
  export). Naming is uniform (`agent-task-milestone-stitching`, `task-log-viewer`,
  `TaskLogMilestone`, `isAcpAgentTask`, `deriveMilestones`) across proposal/design/
  specs/tasks. Task acceptance criteria match the design's leans (e.g. failed-flag on
  `failureReason`, download serialization `<timestamp> [session] <label>: <detail>`).

- **Feasibility** — design code citations spot-checked against source and confirmed
  accurate:
  - `TaskProgressPanel.tsx:230-244` reconstructs `origin` and drops `sessionName`/
    `classification` (the field-drop is real).
  - `WorkflowView.tsx:152,165` retains `sessionName` and `classification` (the mirror
    to copy is real).
  - `TaskLogPanel.tsx:14-19` props are exactly `{ issueNumber, taskId, workflowRunId?,
    taskStatus? }`.
  Acquisition path (`useWorkflowRunSessions`, `WorkflowRunSession` summary fields,
  `mergeTaskLogDelta`) is unchanged — purely additive.
  Task granularity: one cohesive Web feature slice (Phase 3b) with implementation and
  its unit + a11y tests bundled together — **not** over-split (no "define interface" /
  "extract class" / "register DI" / standalone test tasks). Splitting would create
  artificial seams inside a tightly coupled view-layer projection; the single-task
  shape matches the proposal's "pure Web additive projection" framing.

- **Dependency completeness** — single task `T-001` with `dependsOn: []` (correct for
  the only task). No cycles possible.

## Repaired Items

None. No safe repairs were required — artifacts are internally consistent, fully
traceable to the issue, and the design's cited code references verified accurate
against the current source.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: `agent-task-milestone-stitching` spec phrases the session-end milestone
  trigger as "when the resolved session has reached an end `status`", while design D4
  (and task AC #3) emit on `completedAt` non-null, justifying the choice because
  `WorkflowRunSession.status` is a free-form string. The design's interpretation is
  sound and self-justified, and the milestone still carries `status` verbatim as the
  spec requires, so this is not a defect.
  SuggestedAction: Optionally tighten the spec wording to name `completedAt` as the
  emission trigger so the spec reads exactly like the design/task. No change needed
  for acceptance.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: design Risk R5 flags a latent `new Date().toISOString()` real-time use
  inside `mergeTaskLogDelta` (`TaskLogPanel.tsx:82`) that `design/testing.md`
  discourages. It is correctly scoped **out** of this issue (this change does not
  touch the merge path).
  SuggestedAction: Track a future issue to inject a `now`/`TimeProvider` into the
  merge path so the latent real-time use is removed.
  Status: follow-up

<promise>PASS</promise>
