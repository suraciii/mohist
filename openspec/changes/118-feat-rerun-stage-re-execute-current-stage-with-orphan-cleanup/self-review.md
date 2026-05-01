## Self-Review: 118-feat-rerun-stage-re-execute-current-stage-with-orphan-cleanup

### Completeness

- All 3 proposal capabilities (`rerun-stage`, `http-api`, `web-ui`) have corresponding spec files with requirements and scenarios.
- All specs have tasks covering them: T-001 covers `rerun-stage` orphan cleanup, T-002 covers `http-api` endpoint, T-003+T-004 cover `web-ui`.
- Edge cases covered: draft/done rejection (http-api), agent-running guard (409), closed issue reopen, missing worktree creation, no-op cleanup when no orphan sessions exist.

### Consistency

- Proposal Capabilities match spec directory names exactly (`rerun-stage/`, `http-api/`, `web-ui/`).
- Task spec references align with spec files and their requirement headings.
- Design decisions D1–D5 trace directly to task notes and descriptions (D1: API handler, D2: `failRunningByIssueId`, D3: `resumePipeline`, D4: reject draft/done, D5: mutation pattern).
- Naming is consistent across all artifacts: `failRunningByIssueId`, `resumePipeline`, `rerunIssue`, `rerunMutation`.
- All three spec files use `## ADDED Requirements` correctly (new capabilities and new requirements on existing capabilities).

### Feasibility

- T-001 only adds a method to an existing repo class (`CoderSessionRepo`) — no external dependencies.
- T-002 depends on T-001's `failRunningByIssueId()` and uses existing `resumePipeline()`, `checkpointRepo`, `worktreeManager` — all verified present in the codebase.
- T-003 depends on T-002 (needs backend endpoint to exist) — correct ordering.
- T-004 depends on T-003 (needs `rerunIssue()` API client method) — correct ordering.
- All tasks follow existing code patterns verified in the codebase (retry handler, reopen handler, useMutation pattern).

### Dependency Graph

- Linear chain: T-001 → T-002 → T-003 → T-004.
- Every non-first task has `dependsOn` pointing to a strictly lower-priority task.
- No cycles, no forward dependencies.
- All task IDs referenced in `dependsOn` exist in the task list.

### Verdict: PASS
