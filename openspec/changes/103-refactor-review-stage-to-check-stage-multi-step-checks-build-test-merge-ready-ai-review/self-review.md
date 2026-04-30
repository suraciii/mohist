# Self-Review Report

## Result: PASS

## Completeness: PASS

- All 10 scope items from the Issue are covered by specs and tasks
- New capabilities (check-suite, check-results-panel) fully specified with all requirements from the Issue's Design section
- Modified capabilities (pipeline-model, workflow-config, http-api, web-ui) have delta specs
- Edge cases covered: auto-fix disabled, timeout, auto-fix exhaustion, disabled checks (ff-merge, ai-review), force approve, MergeQueue failure
- Post-approval flow (MergeQueue integration) specified and tasked
- The existing reject endpoint (`POST /:number/reject`) already handles Check→Build stage regression — confirmed by reading `api/issues.ts:1077-1078` where `rejectedStage === Stage.Review` triggers `updateStage(issue.id, Stage.Build)`. After T-001 renames the enum, this becomes `Stage.Check` automatically.

## Consistency: PASS

- Proposal's 6 capabilities (2 new, 4 modified) match exactly 6 spec directories
- All task `spec` references map to real requirement headings in specs (verified heading text matches)
- Design decisions D1-D7 align with spec requirements (in-place rename, controller-based suite, MergeQueue on approve, ChecksConfig in WorkflowConfig, approvalState.output storage)
- Naming consistent: "check" not "review", CheckResult/CheckSuiteOutput/CheckResultsPanel/ChecksConfig across all artifacts

## Feasibility: PASS

- T-001 through T-011 are each scoped to coherent file groups completable in one agent iteration
- No two tasks write to the same file in the same priority level (T-004 and T-005 both write to workflow-controller.ts but T-005 depends on T-004 so they run sequentially)
- Implementation approach reuses existing patterns: coder agent spawning (D3), canFastForward (D4), MergeQueue (D5), approvalState.output (D7)
- Known gap: SSE real-time check status updates (check-results-panel spec "Check status real-time updates via SSE") are specified but no dedicated task covers the frontend SSE consumption. The design notes this as an open question. Panel renders from approvalState.output on page load; SSE is an enhancement for a follow-up.

## Dependency Completeness: PASS

- All 10 non-first tasks have at least one `dependsOn` entry
- All `dependsOn` reference existing task IDs with strictly lower priority numbers
- No cycles in the dependency graph (verified by tracing each task)
- Input/output trace:
  - T-001 produces Stage.Check enum → consumed by T-002, T-003, T-004, T-007, T-008
  - T-002 produces ChecksConfig → consumed by T-004
  - T-003 produces CheckResult/CheckSuiteOutput types → consumed by T-004, T-007, T-009
  - T-004 produces runPipelineCheckStage with Build & Test → consumed by T-005, T-006
  - T-005 produces Merge Ready + AI Review checks → consumed by T-011 (tests)
  - T-006 produces approve→MergeQueue wiring → consumed by T-010
  - T-008 produces frontend Stage.Check → consumed by T-009
  - T-009 produces CheckResultsPanel → consumed by T-010

## Quality: PASS

- All specs use SHALL language consistently
- All scenarios use exact `####` heading format (verified line by line)
- All tasks have 4-10 verifiable acceptance criteria
- tasks.json includes all required fields (id, title, spec, description, acceptanceCriteria, priority, mode, type, output, dependsOn, passes, notes)

## Fixes Applied

1. **T-001 scope narrowed**: Removed overlap with T-002 (workflow-loader DEFAULT_WORKFLOW), T-007 (status.ts issuesByStage), and T-008 (session-header). T-001 now scoped to types/index.ts, db/migrations.ts, api/issues.ts (approve/reject handlers), and cli/commands/issue.ts. Added explicit "Do NOT modify" note.

2. **T-005 description clarified**: Added explicit mention of adding `worktreeManager` to `WorkflowControllerOptions` and wiring it in `agent-runner-service.ts`, since the Merge Ready check needs it.

3. **T-006 description clarified**: Added note that existing reject endpoint handles Check→Build regression after T-001 renames the enum, so no new endpoint is needed. Removed vague "keep mergeBackFn for backward compat" text.

4. **T-010 description clarified**: 'Back to Build' now explicitly uses `POST /:number/reject` (existing endpoint), not a new endpoint. Removed "Ensure the stage regression endpoint exists" text.
