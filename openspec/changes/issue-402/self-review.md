# Self Review Report

## Result: FAIL

## Repaired Items

None. No safe repairs were available for the issues found.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: feasibility
  Evidence: T-002's acceptance criteria are internally contradictory. Criterion 1 requires `buildActivityEvents` to "produce ActivityEvent entries ... entries distinguish issue-state, workflow-stage, agent-session, runner, and failure evidence without relabeling current-state snapshots as unavailable transitions." This requires a project-scoped recorded-event input. Criterion 8 states "The feature uses only existing activity, session, issue/workflow snapshot, and runner projections and endpoints; it adds no server, runner, CLI, API/DTO, event-recording, event-emission, transcript-recording, or event/session-subscription behavior." The design (D1, line 42) explicitly states: "Selecting or creating the project-scoped recorded-event input is a blocking prerequisite. It cannot be resolved safely while retaining the proposal's 'Web-only, no API/DTO/query change' impact claim." The Open Questions (line 161) confirm: "This must be resolved before T-002 starts." No project-scoped recorded-event source exists today; durable issue/workflow events are available only per-issue (`/api/projects/:projectRef/issues/:number/events`), not cross-issue. Therefore T-002 cannot simultaneously satisfy criterion 1 (produce issue-state/workflow-stage events from recorded events) and criterion 8 (use only existing endpoints, no new API/DTO) without the open question being resolved. The task notes acknowledge the prerequisite ("must be resolved before this task starts"), but the acceptance criteria remain contradictory as written.
  SuggestedAction: Either (a) resolve the open question about the project-scoped recorded-event source and update T-002's constraints accordingly, (b) split T-002 into a phase deliverable using only existing snapshots (agent-session, runner, approval events) and a blocked phase for recorded-event evidence (issue-state, workflow-stage, failure lifecycle) pending the architectural decision, or (c) relax criterion 8 to explicitly permit the recorded-event data source once selected, removing the contradiction.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: The proposal's Impact section states "No API, DTO, query, or event-recording changes; both views consume existing projections and endpoints." The design's D1 (line 42) and Open Questions (line 161) acknowledge that a project-scoped recorded-event input is needed for T-002 and "cannot be resolved safely while retaining the proposal's 'Web-only, no API/DTO/query change' impact claim." This is a direct inconsistency between the proposal's stated impact and the design's findings. Once item-1 is resolved, the proposal's Impact section should be updated to reflect the actual scope.
  SuggestedAction: Update the proposal's Server/runner/CLI impact line to acknowledge the T-002 recorded-event prerequisite and any resulting scope change once the open question is resolved.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: Both tasks reference only the first requirement heading of their respective spec files (T-001: `#coder-session-has-a-stable-scannable-layout-with-defined-evidence-regions`, T-002: `#activity-distinguishes-execution-event-types-not-only-session-cards`). Each task actually covers all requirements in its spec. The references are not incorrect but are imprecise -- a reader following the anchor sees only the first requirement, not the full coverage.
  SuggestedAction: Consider referencing the spec file path without a heading anchor, or listing all covered requirement headings in the task's `spec` field or notes.
  Status: follow-up

<promise>FAIL</promise>
