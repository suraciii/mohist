# Self-Review: issue-627

## Verdict

PASS. No must-fix problem remains in the plan relative to issue 627 and its acceptance criteria.

## Re-Review Dispositions

### 1. Authoritative late-result ingress

Fixed properly. `design.md:70-78` now selects the existing `POST /api/runner/{runnerId}/report` route, defines the additive `RunnerReportRequest` binding fields, requires the complete Agent binding for terminal Agent reports, preserves the non-Agent paths, and describes how the Runner journal and Workflow binding path carry the same executed-turn identity. `tasks.json` T-004 (`:72-94`) and the capability spec (`spec.md:123-130`) make that contract and its stale side-effect-free behavior executable. The previous open transport choice is removed from `design.md`.

### 2. Concurrent unknown attempts

Fixed properly. The design test protocol (`design.md:94`), T-001/T-002/T-004 acceptance criteria (`tasks.json:15`, `:34`, and `:82-83`), and the capability scenario (`spec.md:38-44`) now require two concurrent unknown attempts on one capacity-limited Runner, both durable blocked transitions, both identity tuples, same-boundary active-work and slot release, cleanup failure coverage, replacement capacity, and non-reoccupying late receipts.

## Review Dimensions

### Issue Basis

Checked, no issue. I read the complete issue body and all current comments before judging the artifacts. The review basis is the durable unknown deadline, active-work and Runner-slot release, preservation of unknown/stop identity and disposition, full-fence late-result arbitration, and deterministic fake-time/failure-injection coverage. The two-concurrent-attempt and same-boundary requirements from the issue comments are included in the revised plan.

### Coverage

Checked, no issue. The proposal, design, capability spec, and task list cover:

- exactly-once Unknown-to-Blocked deadline settlement and assignment release;
- preservation of task/run lifecycle state, execution identity, stop facts, reason, and deadline;
- exclusion from claims, redelivery, active-work projections, and Runner capacity;
- idempotent snapshot, reminder, stage/resource cleanup and repair of legacy blocked assignments;
- blocked/unknown status, Issue/Inbox attention, event replay, and reason/detail projections;
- full-fence late success/failure arbitration with duplicate, incomplete, mismatched, and physical-only receipts;
- the explicit two-concurrent-unknown live regression; and
- fake-time, grain replay, failure-injection, capacity, API, projection, and non-Agent regression tests.

### Correctness

Checked, no issue. The selected release boundary combines the Blocked settlement transition and assignment removal in one durable run save while retaining the task/work/Runner and Agent execution facts needed for late routing. Keeping task and WorkflowRun lifecycle status Running is consistent with the plan's blocked-settlement projection model, while `HasUnresolvedAgentResult` and `HasDispatchableWork` prevent replacement claims.

The active-work plan follows the current assignment-based ownership model and updates the grain read model, persisted work projection, database queries, Runner status/capacity, and polling behavior. Cleanup is explicitly ordered after the durable boundary and is independently retried. The late-result path reconciles due deadlines first, requires the complete execution tuple, applies only the original task outcome, and forbids ownership reacquisition or side effects for stale receipts.

### Current-Code Consistency

Checked, no issue. The plan targets real current boundaries: `WorkflowRun.Assignment`, `BlockUnresolvedAgentResult`, `WorkflowGrain` settlement reconciliation, `WorkflowRunQuerier`, `WorkflowRunWorkProjectionBuilder`, `WorkflowReadModel`, `RunnerGrain`, `DispatchService`, `WorkflowReportService`, `RunnerReportRequest`, `AgentExecutionBinding`, and the durable Runner result journal. The proposed serialized and HTTP additions are nullable/additive and the plan explicitly preserves the existing non-Agent report behavior.

### Task Breakdown

Checked, no issue. T-001 establishes the durable state boundary and cleanup contract. T-002 depends on it and updates active-work and capacity behavior. T-003 independently updates blocked consumer projections after T-001. T-004 depends on the state and ownership changes before implementing late-result arbitration. Each task links to a capability requirement and includes verifiable state, projection, race, failure-injection, or ingress assertions; the revised concurrent regression is no longer implicit.

## Observations

- `design.md:115-119` leaves the public blocked `Reason` shape, workspace routing after assignment release, and maintenance-sweep/metrics treatment open. The normative behavior is still sufficiently defined as a stable blocked category plus persisted reason/detail and deadline, so these do not block the issue goals.
- `DispatchService` currently unions runner-reported `inFlight` and `awaitingAck` keys into poll capacity after server-side active-work reconciliation. T-002 covers stale snapshots and post-release polling, but implementation tests should also include a stale reported work key so a released workflow cannot reduce spare poll capacity solely because the Runner has not yet cleared its local report. This is an edge-consistency observation, not a must-fix against the issue's server-side activeWorks and used-slot acceptance.
- The migration rollback text (`design.md:107-113`) correctly preserves cleared assignments and non-dispatchability, but a pre-change server binary still has the old partial report ingress. Rollback should therefore be operationally coordinated with the full-fence report contract; this does not affect the forward plan's selected ingress or acceptance coverage.

<promise>PASS</promise>