---
purpose: "Describe workflow scheduling at the grain-interface level."
include:
  - "Grain responsibilities and public grain interfaces."
  - "Discovery, assignment, pull delivery, report, supervision, and recovery."
  - "ASCII diagrams and swimlanes that show grain-to-grain interactions."
exclude:
  - "WorkflowRun/domain model internals; keep them in the Domain Model chapter only."
  - "Database schemas, persistence implementation, migrations, and storage tables."
  - "HTTP API payloads, Web UI behavior, and user-facing copy."
  - "Low-level code details unless they clarify an interface contract."
style:
  - "Prefer diagrams over prose."
  - "Keep text short and human-readable."
  - "Use workflowRunId for identifiers in interaction diagrams."
---

# Workflow Scheduling

Grain-level scheduling. Discovery, assignment, delivery, report, supervision, recovery.

## Model

```text
WorkflowGrain
  owns work lifecycle + progression
  owns Assignment (the single assignment truth)
  serves work on pull; passive between polls
  no timer, no runner concept — consumes only work results
  never queries runner state

RunnerGrain
  supervises runner process liveness only (heartbeat, online/offline)
  tracks its outstanding works AND their dispatch snapshots; on detected loss synthesizes their failure via the normal report channel
  discovers assigned-to-me and claimable workflows by querying the store
  claims via AssignRunnerAsync; pulls new work from assigned workflows via GetNextWork + Claim
  recovers held work from its own snapshots on process restart — never asks the workflow to reconstruct a dispatch
  does not supervise work progress (that is the runner process's job)

runner process (physical)
  executes work; spawns subprocesses
  owns execution timeout (progress-aware): kill hung/runaway work, report failure
  no supervision duty
```

```text
assignment truth = WorkflowRun.Assignment      (one record; the single assignment write)
runner list      = in-memory cache; rebuilt from a query over the store
the store        = shared query layer: runners read it to discover assigned-to-me and claimable workflows
```

## Interfaces

```text
IWorkflowGrain
  AssignRunnerAsync(runnerId) -> Assigned | Rejected   # called by runner; sets Assignment
  GetNextWork(runnerId) -> WorkItem?                    # offer: next PENDING work; no state change
  Claim(runnerId, workId) -> workId | null              # runner confirms durable hold; workflow marks Running + persists
  ReportResultAsync(runnerId, workId, result)           # only work results; never runner state

IRunnerGrain
  RegisterAsync(info)
  HeartbeatAsync()
  PollAsync() -> WorkDispatch?                          # process pulls; grain serves held-work (snapshot) or gets new work
  ReportResultAsync(workflowRunId, workId, result)      # relay to workflow; on loss, synthesize failure for outstanding works
```

Delivery is pull. No push from WorkflowGrain to runner.
Discovery is store queries, not grain calls: a runner reads workflow records by their Assignment field — present-and-matching for its own, absent for claimable.

Two-phase dispatch — offer then claim — makes `Running ⟺ durably held` a flow invariant rather than a reconciled one:

```text
GetNextWork returns a PENDING work item but does NOT transition it. The
task stays PENDING until the runner durably registers the work (its own
grain state + ledger) and calls back Claim. Only then does the workflow
mark the work Running. So a Running task is always backed by a runner
record that already exists; there is no window where the workflow says
"running" but no runner has claimed responsibility.
```

The runner is the authoritative holder of work it has claimed. Once
claimed, the runner keeps a dispatch snapshot; recovery after a process
restart is the runner's own business — the workflow is not a dispatch
reconstruction service.

## Project Scan

```text
project-bound runner -> query claimable WHERE ProjectId = @p
global runner        -> query across known projects (in-memory dir + persisted list)
```

Persisted project ids keep claimable discovery working after server restart.

## Assignment

A workflow is **claimable** when its Assignment is null and it is in a runnable state — a query over the Assignment field, not a separate pool:

```text
WorkflowRuns WHERE Assignment IS NULL AND Status = <runnable>   [AND ProjectId = @p]
```

A runner with spare capacity claims a claimable workflow: it scans the store, picks a candidate, and calls `AssignRunnerAsync` directly. The WorkflowGrain sets `Assignment` (the single assignment truth) and persists.

```text
RunnerGrain                                       WorkflowGrain
    | scan claimable (store query)                      |
    | pick workflowRunId                                |
    | AssignRunnerAsync(runnerId)                       |  ← single assignment write
    |-------------------------------------------------->|
    |                                                   | set Assignment (1:1, lifetime); persist
    | return Assigned | Rejected                        |
    |<--------------------------------------------------|
    | Assigned: add to in-memory list (cache)           |
    | Rejected: try next candidate                      |
```

```text
AssignRunnerAsync (idempotent arbiter; truth = WorkflowRun.Assignment):
  unassigned + runnable            -> set Assignment, Assigned
  already assigned to same runner  -> Assigned
  assigned to another runner       -> Rejected
  not runnable                     -> Rejected
```

One record, one write: `Assignment` lives on the WorkflowGrain and is the only assignment truth. Claimable is exactly-once — a workflow is claimable only before its first assignment, and assignment is sticky for life (never null again), so "claim once, never re-claimable" is a property of the data, not an enforced queue rule. Optimistic claiming: concurrent runners may pick the same candidate; the arbiter admits exactly one, the rest retry. The runner's in-memory add is a cache; the periodic discovery scan is authoritative.

## Discovery

A runner finds the workflows assigned to it by reading the store — the same records that hold the assignment truth. A fieldSelector-style query, not a grain call:

```text
WorkflowRuns WHERE Assignment.RunnerId == <me>     (indexed column derived from the record)
```

The runner scans periodically and on activation, and reconciles its in-memory list from the result. The list is a disposable cache: stale entries are safe (GetNextWork/Claim are gated by the WorkflowGrain's own Assignment), and the scan rebuilds it after any loss.

## Pull Work

RunnerGrain serves its process from two sources: work the runner already
holds (dispatch snapshot, recovered from local state) and new work pulled
from an assigned workflow.

```text
runner process     RunnerGrain                WorkflowGrain
    |                  |                           |
    | PollAsync()      |                           |
    |----------------->|                           |
    |                  | outstanding held work?    |
    |                  | (own _worksState)         |
    |                  |   yes -> return snapshot  |  ← recovery; no workflow call
    |                  |   no  -> fall through     |
    |                  | GetNextWork(runnerId)     |
    |                  |--------------------------->|
    |                  |                           | PENDING work -> return WorkItem (no state change)
    |                  |                           | none        -> null
    |                  | return WorkItem?          |
    |                  |<--------------------------|
    |                  | register hold:            |
    |                  |   _worksState + ledger    |  ← durable claim, BEFORE telling workflow
    |                  |   (with dispatch snapshot)|
    |                  | Claim(runnerId, workId)   |
    |                  |--------------------------->|
    |                  |                           | PENDING -> Running + persist; return workId
    |                  |                           | gone    -> null (offer overtaken)
    |                  |                           | Running -> idempotent, return workId
    |                  | return workId | null      |
    |                  |<--------------------------|
    |                  | null -> rollback hold     |
    |                  | build dispatch, return    |
    | return WorkDispatch?                        |
    |<-----------------|
```

No forward call from WorkflowGrain to runner. The runner's assigned set is the discovery cache; WorkflowRun.Assignment is the truth.

The dispatch snapshot stored at claim time is the full rendered dispatch
(prompts, variables, payload). It is what the runner process needs to
execute; storing it at claim means recovery needs nothing from the
workflow grain.

## Work State Machine

```text
PENDING --claim--> RUNNING --report(success|fail)--> COMPLETED | FAILED
```

- PENDING: work exists, waiting to be pulled. `GetNextWork` surfaces it.
- RUNNING: a runner has durably claimed it. `Running ⟺ claimed` is a flow invariant: the workflow only flips a task to Running inside `Claim`, which the runner calls *after* persisting its hold. There is no "running but unclaimed" window.
- COMPLETED | FAILED: report received; workflow advances.

Reports arrive from two producers, indistinguishable to the grain:

- the executing runner process (normal completion, or its own progress-aware timeout failure);
- RunnerGrain, synthesizing failure for outstanding works when it detects the runner is lost.

WorkflowGrain arms no timer. It never learns why a work failed — only that it did.

## Supervision

Supervision is split by level. Nothing crosses levels, and no runner state ever reaches WorkflowGrain.

```text
work execution timeout   runner process     progress-aware: kill hung/runaway work, report failure
runner process liveness  RunnerGrain        heartbeat -> online/offline; on loss synthesize failure for outstanding works
(work timeout)           WorkflowGrain      none — no timer, consumes only work results
```

The runner process is the only party with progress signal (tokens streaming, subprocess alive), so only it judges "this work is too slow / wedged" — never the server. RunnerGrain judges only "the runner is gone" (heartbeat loss) and, as the dead runner's executor-of-last-resort, closes out its outstanding works as plain failure through the normal report channel.

WorkflowGrain never knows a runner was lost. It receives identical `failed` reports whether the runner failed on its own or was closed out by RunnerGrain.

## Report

```text
runner process        RunnerGrain              WorkflowGrain
    |                     |                          |
    | execute             |                          |
    | ReportResultAsync(workflowRunId, workId, result)|
    |-------------------->|                          |
    |                     | ReportResultAsync(runnerId, workId, result)
    |                     |------------------------->|
    |                     |                          | validate assigned runner
    |                     |                          | RUNNING -> COMPLETED | FAILED
    |                     |                          | advance / arm repair
    |                     | return response          |
    |                     |<-------------------------|
    |<--------------------|
```

Late or duplicate report for an already-terminal work is ignored (idempotent by workId + attempt).

## Assignment Lifecycle

One workflow, one runner, for the run's life. Sticky: never released, never reassigned.

An assigned workflow is held by its runner for the whole run — through idle, approval gates, and work-item boundaries. The runner polls by state (busy → return work; idle/gated → null) and never releases the workflow back. Capacity gates concurrent execution (active works), not assigned-workflow count: a runner may hold many idle assigned workflows while executing up to its slot count.

```text
assigned            -> stays assigned through work items, idle periods, and gates; must flow continuously
work stalls         -> runner process progress-aware timeout fails the WORK (not the assignment); workflow advances
runner transient loss (process restart) -> runner grain still holds the dispatch snapshot; on restart the process re-receives held work from the snapshot and resumes. heartbeat lost: RunnerGrain closes out in-flight works as FAILED
runner permanently gone -> out of scope: user starts a fresh run (new workflow, new assignment)
```

- Resume: a returning runner process receives its held work from the runner grain's dispatch snapshots (no workflow call); if closeout already happened (heartbeat loss synthesized FAILED), the next new work comes via GetNextWork.
- Permanent loss (runner never returns): user starts a new run; no automatic failover.

Pipeline rule: once claimed, the workflow must flow continuously — every work reaches COMPLETED or FAILED (by the runner's own report, or by RunnerGrain's closeout on loss). Any stall after assignment is a bug. Pending before assignment is normal waiting, not a stall.

## Recovery

Retry + idempotency throughout. An assigned workflow never becomes claimable again.

**Principle: the runner is the authoritative holder of work it has claimed. Recovery is the runner's own business — the workflow is not consulted.**

At claim time the runner stores the full rendered dispatch as a snapshot
alongside its work record. On any recovery the runner rebuilds the dispatch
from its own snapshot; it never asks the workflow to reconstruct it.

```text
claim call lost                  -> runner retries AssignRunnerAsync (idempotent)
offer lost (GetNextWork returned, runner crashed before registering)  -> task still PENDING; next poll re-offers (no dirty state)
claim registered, then runner crashes before Claim()  -> runner holds a record for a still-PENDING task; the record is stale and cleared by timeout/stale sweep; workflow unaffected (task never left PENDING)
report lost                      -> runner re-reports; workflow dedups (idempotent)
work wedged / runaway            -> runner process progress-aware timeout -> reports FAILED
runner process restarts mid-work -> runner grain still holds the dispatch snapshot; next poll re-delivers it from the snapshot (no workflow call); runner process resumes execution
process dies mid-work (heartbeat lost) -> no report; RunnerGrain detects heartbeat loss -> synthesizes FAILED for outstanding works
```

Runner-loss detection must be persistent (Orleans reminder, not a grain timer) and keyed off persisted heartbeat state, so it survives silo restart and still catches a permanently-gone runner.

Because recovery never touches the workflow, the workflow grain needs no
"get active work" path for dispatch reconstruction. `GetActiveWorkAsync`
exists only for non-recovery concerns (e.g. artifact upload binding) and is
not part of the dispatch/recovery contract.

Before start (no runner): pending is normal; the workflow is claimable and waits.

## 实装差距

正文描述目标设计。以下是与当前代码的差距，由后续 issue 推进落地。

**已交付**（offer/claim 两阶段 dispatch）：
- `GetNextWork`（offer）+ `Claim`（标 Running）两步；`Running ⟺ claimed` 由流程顺序保证。
- offer 不改状态、不持久化、不取 lock；claim 落盘后才标 Running。
- check workId 确定化（`checks-{stage}`），使 offer 可预知 workId。

**未交付**（dispatch 快照自治恢复）：
- workflow work 的 dispatch 快照尚未存储——`RunnerWork.DispatchSnapshot` 当前只填充 agent-job，workflow work 领取时不存。恢复仍走 `GetActiveWorkForRunner`（重入分支）和 `RecoverWorkItemFromActiveWorkAsync`/`RecoverWorkItemFromRun`（report 时重建），即恢复仍问 workflow。
- `PollWorkAsync` 仍含 `GetActiveWorkForRunner` 重入分支（spec 要求 `GetNextWork` 只返回 Pending，恢复走快照）。
- capacity gate 仍经 `RemoveStaleWorkflowWorksAsync` 预清理后读 `_worksState`（spec 要求直接读 workflow 侧 `CountRunningAssignedToAsync`，不受 runner stale 记录影响）。

落地后删除：`GetActiveWorkForRunner`、`RecoverWorkItemFromActiveWorkAsync`/`RecoverWorkItemFromRun`/`RecoverActiveWorkflowWorkAsync`、`RemoveStaleWorkflowWorksAsync`。
