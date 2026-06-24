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
  tracks its outstanding works; on detected loss synthesizes their failure via the normal report channel
  discovers assigned-to-me and claimable workflows by querying the store
  claims via AssignRunnerAsync; pulls work from assigned workflows
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
  PollWork(runnerId) -> WorkDispatch?                   # assigned runner pulls work
  ReportResultAsync(runnerId, workId, result)           # only work results; never runner state

IRunnerGrain
  RegisterAsync(info)
  HeartbeatAsync()
  PollAsync() -> WorkDispatch?                          # process pulls; grain queries its assigned workflows and PollWork
  ReportResultAsync(workflowRunId, workId, result)      # relay to workflow; on loss, synthesize failure for outstanding works
```

Delivery is pull. No push from WorkflowGrain to runner.
Discovery is store queries, not grain calls: a runner reads workflow records by their Assignment field — present-and-matching for its own, absent for claimable.

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

The runner scans periodically and on activation, and reconciles its in-memory list from the result. The list is a disposable cache: stale entries are safe (PollWork is gated by the WorkflowGrain's own Assignment), and the scan rebuilds it after any loss.

## Pull Work

RunnerGrain pulls work from its assigned workflows; serves it to its process.

```text
runner process     RunnerGrain                WorkflowGrain
    |                  |                           |
    | PollAsync()      |                           |
    |----------------->|                           |
    |                  | PollWork(runnerId)        |
    |                  |--------------------------->|
    |                  |                           | PENDING     -> STARTED, return it
    |                  |                           | in-flight   -> return for resume
    |                  |                           | none        -> null
    |                  | return WorkDispatch?      |
    |                  |<--------------------------|
    | return WorkDispatch?                        |
    |<-----------------|
```

No forward call from WorkflowGrain to runner. The runner's assigned set is the discovery cache; WorkflowRun.Assignment is the truth.

## Work State Machine

```text
PENDING --pull--> STARTED --report(success|fail)--> COMPLETED | FAILED
```

- PENDING: work exists, waiting to be pulled.
- STARTED: pulled by a runner. Stays STARTED until a report arrives.
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
    |                     |                          | STARTED -> COMPLETED | FAILED
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
runner transient loss (process restart) -> heartbeat recovers: runner resumes pulling; heartbeat lost: RunnerGrain closes out in-flight works as FAILED, re-pulled on recovery
runner permanently gone -> out of scope: user starts a fresh run (new workflow, new assignment)
```

- Resume: same runner returns, pulls PENDING/in-flight work, continues.
- Permanent runner loss is a user operation (start a new run). No automatic failover or reassignment.

Pipeline rule: once claimed, the workflow must flow continuously — every work reaches COMPLETED or FAILED (by the runner's own report, or by RunnerGrain's closeout on loss). Any stall after assignment is a bug. Pending before assignment is normal waiting, not a stall.

## Recovery

Retry + idempotency throughout. An assigned workflow never becomes claimable again.

```text
claim call lost                  -> runner retries AssignRunnerAsync (idempotent)
work pull lost                   -> runner polls again; workflow re-serves (idempotent)
report lost                      -> runner re-reports; workflow dedups (idempotent)
work wedged / runaway            -> runner process progress-aware timeout -> reports FAILED
process dies mid-work            -> no report; RunnerGrain detects heartbeat loss -> synthesizes FAILED for outstanding works
```

Runner-loss detection must be persistent (Orleans reminder, not a grain timer) and keyed off persisted heartbeat state, so it survives silo restart and still catches a permanently-gone runner.

Before start (no runner): pending is normal; the workflow is claimable and waits.

After start (has runner): every work reaches COMPLETED or FAILED — by the runner's own report, or by RunnerGrain's closeout on loss.
