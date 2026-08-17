# Self-Review: issue-627

## Verdict

FAIL. The plan has must-fix gaps relative to the issue's late-result and concurrent-capacity acceptance requirements.

## Must-Fix Findings

### 1. The authoritative late-result ingress is undecided

`design.md:70-76` requires a complete WorkflowRun/task/work/Runner/AgentSession/AgentTurn/runtime/runtime-session fence, but `design.md:115` leaves the authoritative transport undecided between additive `RunnerReportRequest` fields and an AgentSession callback. `tasks.json` T-004 (`:73-90`) repeats that choice as a human decision required before implementation.

This is acceptance-critical, not an implementation detail. The current code confirms the gap: `RunnerReportRequest` has no Agent execution binding (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:1003-1016`), `WorkflowReportService` converts a runner result directly into `ReceiveTaskReportAsync` using only runner/work/task identity (`packages/server/src/Mohist.Server/Runner/Services/WorkflowReportService.cs:38-64`), and that grain path can report a blocked attempt before any full binding check (`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:250-295`).

This violates the issue requirement that a late result be accepted only through the existing identity fence and that insufficiently identified receipts not be accepted. It also leaves T-004's first acceptance criterion unverifiable. The plan must settle the authoritative ingress and its additive wire contract before implementation, including how the runner/session supplies every required binding field and how non-Agent reports remain unchanged.

### 2. The required concurrent-unknown release regression is not explicit

The issue comments add a live acceptance case for two concurrent unknown works (`cmt_d3521c321c484460a14f3a5a878b086a`) and require the post-deadline blocked settlement and Runner active-work/capacity release to be the same exactly-once boundary (`cmt_bb464b719dcd4705935b688fff4c02fd`).

The plan's test scenarios are all singular: `design.md:88-93` describes "an attempt," "a capacity-full Runner," and "the old attempt," while T-001 through T-004 in `tasks.json:11-15`, `:32-35`, and `:76-79` never require two unknown attempts to cross the deadline together. A test of one released workflow plus one different eligible workflow does not prove that both concurrent unknown rows disappear from `activeWorks` and used-slot accounting, nor that repeated settlement of either row cannot release or retain capacity twice.

This leaves the added live acceptance incomplete: the plan must add an explicit fake-time/failure-injection scenario with two concurrent unknown attempts, assert both blocked transitions and both original identities, assert the corresponding Runner active-work and capacity projections are released at the same durable boundary, then verify another work item can claim capacity and matching late receipts remain identity-fenced and non-reoccupying.

## Review Dimensions

### Issue Basis

Checked, no issue. The issue body and all current comments were read before the artifacts. The review basis is the durable deadline transition, active-work/slot release, preservation of unknown/stop identity and disposition, full-fence late-result arbitration, and deterministic fake-time/failure-injection coverage. The comments' concurrent-work and same-boundary requirements are included above.

### Coverage

FAIL due to findings 1 and 2. The proposal, design, and capability spec cover the main state, cleanup, projection, and late-result behaviors. However, the late-result transport needed to satisfy the full-fence criterion is unresolved, and the explicit concurrent regression added by the issue comments is absent from the task acceptance criteria.

### Correctness

FAIL due to finding 1. The assignment-removal approach is consistent with the current Runner queries and projections, and the design correctly keeps task/work/Runner and Agent identity facts after release. It cannot yet be judged correct for late results because the current runner report path accepts only the partial tuple and the plan has not selected the replacement ingress or specified its end-to-end arbitration contract.

### Current-Code Consistency

Checked, no additional issue. The plan names real boundaries in the current codebase: `WorkflowRun.Assignment`, `WorkflowRunWorkProjectionBuilder`, `WorkflowRunQuerier`, `RunnerGrain` runtime status, `DispatchService`, the settlement reminder, and the existing `AgentExecutionBinding` observation path. The planned nullable/additive protocol extension is compatible with the repository's serialized record conventions once the authoritative path is decided.

### Task Breakdown

FAIL. Ordering is checked, no issue: T-001 establishes the durable boundary, T-002 handles active-work/capacity projections, T-003 handles consumer projections, and T-004 depends on T-001/T-002 for late-result arbitration. Completeness and verifiability are not sufficient because T-004 is blocked on an unresolved HITL choice and no task makes the two-concurrent-unknown, same-boundary assertion mandatory.

## Observations

- `design.md:114` leaves the public blocked `Reason` shape open. The normative requirement is clear enough to implement as stable category plus persisted reason/detail, so this is an observation rather than a must-fix finding.
- `design.md:116-117` leaves workspace routing after assignment release and the maintenance sweep/metrics question open. These are operational follow-ups unless implementation reveals a workflow-goal regression; they do not change the required capacity and identity behavior.
- The current `DispatchService` unions runner-reported work keys into poll capacity after database active-work reconciliation (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:84-96`). T-002 should explicitly test a stale reported key after release and define whether it is filtered or acknowledged, because otherwise a local stale report can still reduce poll spare capacity even when the released workflow is absent from server active-work projections. This is recorded as an observation because the issue's direct live evidence is the server-side `activeWorks`/used-slot projection, which T-002 explicitly covers.

<promise>FAIL</promise>
