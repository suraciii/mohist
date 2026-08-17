# Self-Review

## Verdict

FAIL. MF-001 is a must-fix problem; the plan is not ready to build as written.

## Must-Fix Findings

1. **MF-001: A matching post-Blocked `unknown` report can still fail the workflow.**
   The issue explicitly says not to infer or replay Agent results and distinguishes a matching late *authoritative* result from non-authoritative observations ([proposal.md](/home/szf/.mohist/projects/workspaces/wr_9f4a3b9eb5cb4d58aaa7bbb06ac3c062/openspec/changes/issue-628/proposal.md:11); [spec.md](/home/szf/.mohist/projects/workspaces/wr_9f4a3b9eb5cb4d58aaa7bbb06ac3c062/openspec/changes/issue-628/specs/rebase-recovery-branch-integrity/spec.md:85)). The current report path violates that distinction: `WorkflowReportService` converts `InboundReport.Unknown` to its failed fallback whenever `ObserveAgentResultUnknownAsync` returns `Stale` ([WorkflowReportService.cs](/home/szf/.mohist/projects/workspaces/wr_9f4a3b9eb5cb4d58aaa7bbb06ac3c062/packages/server/src/Mohist.Server/Runner/Services/WorkflowReportService.cs:44)). For a matching attempt whose settlement is already `Blocked`, the domain deliberately rejects the observation ([WorkflowRun.Work.cs](/home/szf/.mohist/projects/workspaces/wr_9f4a3b9eb5cb4d58aaa7bbb06ac3c062/packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Work.cs:654)); the fallback is then accepted by `ReceiveTaskReportAsync` and the lifecycle calls `FailTask` ([WorkflowGrain.Reports.cs](/home/szf/.mohist/projects/workspaces/wr_9f4a3b9eb5cb4d58aaa7bbb06ac3c062/packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:267); [WorkflowWorkLifecycle.cs](/home/szf/.mohist/projects/workspaces/wr_9f4a3b9eb5cb4d58aaa7bbb06ac3c062/packages/server/src/Mohist.Server/Workflow/Grains/WorkflowWorkLifecycle.cs:69)). Thus a matching non-authoritative observation can create `TaskFailed` after `Blocked`.

   T-005 covers a matching authoritative late report and mismatched reports, but does not specify or test this matching `unknown` case ([tasks.json](/home/szf/.mohist/projects/workspaces/wr_9f4a3b9eb5cb4d58aaa7bbb06ac3c062/openspec/changes/issue-628/tasks.json:95)). Leaving it unspecified leaves a direct violation of the issue's no-inference goal and the blocked-settlement contract. The plan must explicitly own the report-path behavior and a regression test: after durable `Blocked`, a matching non-authoritative `unknown` observation must remain stale and leave the settlement/projections unchanged, while only an explicitly authoritative success/failure report may settle the task.

## Review Sweep

- **Issue goals and acceptance criteria:** checked, no issue. The canonical issue body and its current comments were read before the artifacts. The branch-integrity, retry identity, deterministic fake-worktree, blocked projection/capacity, late-result fencing, and no-replay/no-slot-policy goals are all represented in the plan.
- **Coverage:** checked, no issue apart from MF-001's missing report-path scenario. T-001 through T-004 cover branch repair, rebase completion, task boundaries, diagnostics, and identity-preserving retries; T-005 covers the added blocked-settlement projection behavior.
- **Correctness:** checked, must-fix MF-001. The planned blocked projection filters are consistent with the existing transactional `AttentionStatus` projection and persisted assignment identity, but the stale-observation fallback can still turn the blocked state into failure.
- **Consistency with the current codebase:** checked, must-fix MF-001. The plan follows the existing `WorkflowRunStore`, `WorkflowRunQuerier`, Runner runtime, and `WorkItemResult` boundaries, but it does not account for the existing `WorkflowReportService` stale-unknown fallback at the late-report boundary.
- **Task breakdown, ordering, and verifiability:** checked, must-fix MF-001. The task graph is valid and acyclic (`T-001` feeds `T-002`/`T-003`, which feed `T-004`; `T-005` is independent), and each task has focused acceptance criteria. T-005 needs the missing matching-non-authoritative-observation criterion/test before the behavior is verifiable.

## Observations

- The conflict resolver path deserves an explicit implementation decision. T-003's universal start/end branch checks can reject a recovery handler while Git is still in a conflict-induced detached `HEAD`, even though the design says the configured resolver can work with the preserved conflict ([design.md](/home/szf/.mohist/projects/workspaces/wr_9f4a3b9eb5cb4d58aaa7bbb06ac3c062/openspec/changes/issue-628/design.md:60)). This is not a must-fix for the issue's stated requirement that detached recovery never reports success and that an explicit retry preserve identity, but the plan should clarify whether the resolver must complete the rebase in one action or is intentionally expected to fail and require explicit retry.
- The task `spec` fields point to one primary requirement each; the conflict/residual and diagnostic requirements are covered by acceptance criteria and design text rather than separate task anchors. This is adequate for behavioral coverage but makes traceability less direct.
- No implementation or repository test suite was run because this review only inspects the plan artifacts and does not modify implementation files.

<promise>FAIL</promise>
