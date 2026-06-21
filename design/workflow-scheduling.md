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
  completion watchdog: no report by work timeout -> work FAILED
  never queries runner state

RunnerGrain
  supervises runner process liveness only (heartbeat, online/offline)
  discovers assigned-to-me and claimable workflows by querying the store
  claims via AssignRunnerAsync; pulls work from assigned workflows
  does not supervise works

runner process (physical)
  executes work; spawns subprocesses
  enforces execution timeout: kill hung subprocess, report failure
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
  ReportResultAsync(runnerId, workId, result)
  NotifyRunnerLostAsync(runnerId)

IRunnerGrain
  RegisterAsync(info)
  HeartbeatAsync()
  PollAsync() -> WorkDispatch?                          # process pulls; grain queries its assigned workflows and PollWork
  ReportResultAsync(workflowRunId, workId, result)      # relay to workflow
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
                       |
                       | no report by declared timeout: watchdog -> FAILED
```

- PENDING: work exists, waiting to be pulled. No timeout (waiting for capacity is normal).
- STARTED: pulled by assigned runner. Watchdog armed with the work's declared timeout.
- COMPLETED | FAILED: report received; workflow advances.
- FAILED by watchdog: no report within timeout. Workflow advances (repair / stage-fail).

The watchdog is a local timer plus "report arrived?". It does not query runner state.

## Supervision

Supervision is split by level. Nothing crosses levels.

```text
subprocess execution    runner process     kill hung subprocess, report failure
work completion         WorkflowGrain      watchdog: no report by timeout -> FAILED
runner process liveness RunnerGrain        heartbeat -> online/offline
```

RunnerGrain supervises the runner process only. A runner serves many workflows; work supervision belongs to the owning WorkflowGrain, which owns the work and its timeout value.

Execution timeout (kill) is in the runner process — only it can kill its own subprocesses.

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
work stalls         -> watchdog fails the WORK (not the assignment); workflow advances
runner transient loss (process restart) -> grain survives; assigned runner resumes pulling on recovery (in-flight failed fast, re-pulled)
runner permanently gone -> out of scope: user starts a fresh run (new workflow, new assignment)
```

- Resume: same runner returns, pulls PENDING/in-flight work, continues.
- Permanent runner loss is a user operation (start a new run). No automatic failover or reassignment.

Pipeline rule: once claimed, the workflow must flow continuously — every work reaches COMPLETED or FAILED (report or watchdog). Any stall after assignment is a bug. Pending before assignment is normal waiting, not a stall.

## Recovery

Retry + idempotency throughout. An assigned workflow never becomes claimable again.

```text
claim call lost                  -> runner retries AssignRunnerAsync (idempotent)
work pull lost                   -> runner polls again; workflow re-serves (idempotent)
report lost                      -> runner re-reports; workflow dedups (idempotent)
work pulled, no report           -> watchdog -> FAILED -> advance
process dies mid-work            -> no report -> watchdog -> FAILED; next work waits for runner
process lost (heartbeat)         -> RunnerGrain -> NotifyRunnerLostAsync -> fail in-flight fast; workflow waits for recovery (never re-claimable)
```

Before start (no runner): pending is normal; the workflow is claimable and waits.
After start (has runner): must progress or fail; the watchdog bounds every work.
