# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: completeness
  Evidence: Spec requirement "Completion metrics exclude AgentActivity as a source" binds **both** the client snapshot and the server endpoint, but only T-002 stated it explicitly. T-001 satisfied it inherently (it reads only issue `status`/`createdAt`/`updatedAt`) yet lacked an explicit, testable criterion tying it to the requirement.
  Verification: Added an acceptance criterion to T-001 — "Snapshot derives counts from issue `status`/`createdAt`/`updatedAt` only; it does NOT source `AgentActivity.summary.completed`/`.failed`." Re-parsed `tasks.json` (valid, T-001 now has 7 ACs).
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: `proposal.md` "What Changes" anticipated the endpoint would "likely derive completion time from workflow run completion events". The design resolved the plan-stage open question the **other** way — bucketing from the durable `IssueEvents` CloudEvents table — with documented rationale (precise `Time`, issue-scoped, survives `ActiveWorkflowRunId` clearing on `Archive`/`Close`). This is the intended proposal→design refinement (the proposal explicitly framed it as "Explore in design"), not a contradiction.
  SuggestedAction: No action required now. Optionally, when the proposal is next touched, soften "likely must derive … from workflow run completion events" to "from the most precise durable completion-time source" so the proposal reads as source-agnostic.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design open question D-OQ1 — confirm that every `Complete()`/`Close()` path (including programmatic/recovery closures) projects to `IssueEvents`. If any path skips `PublishIssueEventsAsync`, the endpoint silently undercounts.
  SuggestedAction: During T-002 implementation, spot-check the event-projection coverage of all terminal transitions before relying on the counts.
  Status: follow-up

## Checks Performed

- Alignment: each "What Changes" bullet traces to an issue acceptance criterion (snapshot→AC1/AC3, endpoint→AC2/AC4, no-AgentActivity→AC5). All issue Non-Goals respected (no Productivity UI, no usage/cost, fixed bucketing only, no prediction).
- Completeness: 5 spec requirements cover all 5 issue acceptance criteria; both tasks collectively cover all 5 requirements; edge cases (flapping/reopen, close-without-run, archived, pre-table events) addressed in design Risks.
- Consistency: one new capability `issue-completion-metrics` (proposal ↔ `specs/issue-completion-metrics/spec.md`); task `spec` anchors match requirement headers verbatim; design D1–D5 align with specs.
- Feasibility: both tasks reuse existing primitives (`useIssues()`, `IssueQuerier` DbContext pattern, `IssueRoutes.*` partial pattern, `IssueEvents` table); no over-splitting (no "define interface"/"register DI"/standalone-test tasks; tests embedded); titles denote functional slices.
- Dependencies: DAG verified — T-001 and T-002 are genuinely independent (two faces of the concern, no shared output), so empty `dependsOn` is correct; no cycles; priorities 1 < 2.
- Spec format: `## ADDED Requirements` / `### Requirement:` / `#### Scenario:` (4 hashtags) all confirmed.

<promise>PASS</promise>
