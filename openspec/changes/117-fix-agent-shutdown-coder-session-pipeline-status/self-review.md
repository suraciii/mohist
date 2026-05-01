## Self-Review: 117-fix-agent-shutdown-coder-session-pipeline-status

### Completeness

- **All 5 proposal capabilities have specs**: `pipeline-stage-timeout` (new), `server-daemon` (modified), `coder-session-tracking` (modified), `reopen-resume` (modified), `agent-pool` (modified).
- **All 5 specs have corresponding tasks**: agent-pool→T-001, coder-session-tracking→T-002/T-003, pipeline-stage-timeout→T-004, reopen-resume→T-005, server-daemon→T-001 (implicit — same shutdown() code change).
- **All 6 root causes from the issue are covered**: (1) agent hang→T-004, (2) shutdown orphans→T-001, (3) coder_session residue→T-002, (4) pipeline status race→T-002, (5) check-stage recovery→T-005, (6) reopen re-hangs→T-005.
- **Edge cases**: abort errors caught per-agent (T-001), existing coder_session rows with null stage acknowledged in design Risks, DB write failure in status guard is non-blocking (T-002).

### Consistency

- **Spec names match proposal Capabilities** section exactly (kebab-case).
- **Design decisions map to tasks**: D1→T-004, D2→T-002, D3→T-003+T-005, D4→T-002, D5→T-004.
- **Task spec references** point to correct spec files.
- **Fixed during review**: Removed stale reference to non-existent `workflow-controller.ts` in proposal Impact. Updated pipeline-stage-timeout spec to reference `executePipeline()` in `AgentRunnerService` instead of non-existent `WorkflowController`. Removed contradictory non-goal about `coder_session.stage` (D3 explicitly implements this).

### Feasibility

- **No circular dependencies**: T-001→T-002→T-004 (linear chain on agent-runner-service.ts), T-003→T-005 (linear chain on stage column), T-006 depends on all.
- **All repo methods needed already exist**: `CoderSessionRepo.findByIssueId()`, `CoderSessionRepo.updateStatus()`, `IssueRepo.hasCompletedCoderSession()`.
- **Task granularity is appropriate**: Each task is a coherent unit targeting 1-2 files, independently testable.

### Dependency Validation

| Task | Priority | dependsOn | Valid? |
|------|----------|-----------|--------|
| T-001 | 1 | [] | Yes (first task) |
| T-002 | 2 | [T-001] | Yes (same file, needs T-001's shutdown changes) |
| T-003 | 3 | [] | Yes (independent file acp-session.ts, runs in parallel) |
| T-004 | 4 | [T-002] | Yes (same file, needs T-002's executePipeline changes) |
| T-005 | 5 | [T-003] | Yes (needs stage column populated by T-003) |
| T-006 | 6 | [T-001..T-005] | Yes (final build verification) |

- DAG: no cycles. All dependsOn reference lower-priority tasks.

### Issues Found and Fixed

1. **Proposal Impact referenced non-existent `workflow-controller.ts`** → Fixed: consolidated into `agent-runner-service.ts`, removed stale `coder-session-repo.ts` mention.
2. **Design Non-Goals contradicted D3/T-003** (said "populating stage column" is excluded but D3 decides to do it) → Fixed: removed from Non-Goals.
3. **pipeline-stage-timeout spec referenced non-existent `WorkflowController`** → Fixed: updated to reference `executePipeline()` in `AgentRunnerService`.

### Verdict

All artifacts are consistent, complete, and feasible after fixes. Ready for implementation.
