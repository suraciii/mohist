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
  serves work on pull; passive between polls
  completion watchdog: no report by work timeout -> work FAILED
  never queries runner state

WorkflowBacklogGrain
  pool of workflows with no runner assigned
  brokers the assignment on claim (writes Assignment onto the workflow)

RunnerGrain
  supervises runner process liveness only (heartbeat, online/offline)
  discovers assigned workflows by querying the store
  pulls work from them on behalf of its process
  does not supervise works

runner process (physical)
  executes work; spawns subprocesses
  enforces execution timeout: kill hung subprocess, report failure
  no supervision duty
```

```text
assignment truth = WorkflowRun.Assignment      (one record; the single assignment write)
backlog          = best-effort pool of unassigned workflows (drift self-heals)
runner list      = in-memory cache; rebuilt from a query over the store
the store        = shared query layer: runners read it directly to discover their assignments
```

## Interfaces

```text
IWorkflowBacklogGrain
  EnqueueAsync(workflowRunId)
  ClaimAsync(runnerId) -> workflowRunId?   # brokers assignment, returns a now-assigned run

IWorkflowGrain
  AssignRunnerAsync(runnerId) -> Assigned | Rejected   # called by backlog; sets Assignment
  PollWork(runnerId) -> WorkDispatch?                   # assigned runner pulls work
  ReportResultAsync(runnerId, workId, result)
  NotifyRunnerLostAsync(runnerId)

IRunnerGrain
  RegisterAsync(info)
  HeartbeatAsync()
  PollAsync() -> WorkDispatch?                          # process pulls; grain fetches from assigned workflows
  ReportResultAsync(workflowRunId, workId, result)      # relay to workflow
```

Delivery is pull. No push from WorkflowGrain to runner.
Discovery is a store query, not a grain call: the runner reads workflow records where the assignment field is its own id.

## Backlog

Backlog = workflows with no runner assigned. Membership by assignment status only.

```text
enter    : new run created, has no runner        (once per run)
leave    : claimed by a runner
re-enter : never
```

An assigned workflow is held by its runner for the whole run — through idle, approval gates, and work-item boundaries. The runner polls by state (busy → return work; idle/gated → null) and never releases the workflow back. Capacity gates concurrent execution (active works), not assigned-workflow count: a runner may hold many idle assigned workflows while executing up to its slot count.

Per-project grain: `WorkflowBacklogKeys.ForProject(projectId)`.

## Project Scan

```text
project-bound runner -> scan its project's backlog
global runner        -> round-robin over known projects (in-memory dir + persisted list)
```

Persisted project ids keep backlog discovery working after server restart.

## Assignment

Runner with spare capacity claims an unassigned workflow from the backlog. The backlog brokers the assignment: it picks a candidate, writes the `Assignment` onto the WorkflowGrain (the single assignment truth), drops the candidate from its pool, and hands the id to the runner.

```text
RunnerGrain            WorkflowBacklogGrain          WorkflowGrain
    | ClaimAsync(runnerId)     |                           |
    |------------------------->|                           |
    |                          | pick unassigned workflowRunId
    |                          | AssignRunnerAsync(runnerId)  ← single assignment write
    |                          |--------------------------->|
    |                          |                           | set Assignment (1:1, lifetime); persist
    |                          | return Assigned | Rejected |
    |                          |<--------------------------|
    |                          | Assigned: drop from pool   |
    |                          | Rejected: try next candidate
    | return workflowRunId?    |                           |
    |<-------------------------|                           |
    | add to in-memory list (cache)                        |
```

```text
AssignRunnerAsync (idempotent arbiter; truth = WorkflowRun.Assignment):
  unassigned + runnable         -> set Assignment, Assigned
  already assigned to same runner -> Assigned
  assigned to another runner    -> Rejected
  not runnable                  -> Rejected
```

One record, one write: `Assignment` lives on the WorkflowGrain and is the only assignment truth. The runner's in-memory add is a cache; the backlog pool is a best-effort index. Drift self-heals — rejected claims are dropped, orphaned unassigned runs (assignment never set) are re-enqueued by their reminder, stale runner entries are rejected at `PollWork`.

## Discovery

A runner finds the workflows assigned to it by reading the store — the same records that hold the assignment truth. This is a fieldSelector-style query, not a grain call:

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

```text
assigned            -> stays assigned through work items, idle periods, and gates; must flow continuously
work stalls         -> watchdog fails the WORK (not the assignment); workflow advances
runner transient loss (process restart) -> grain survives; assigned runner resumes pulling on recovery (in-flight failed fast, re-pulled)
runner permanently gone -> out of scope: user starts a fresh run (new backlog entry, new assignment)
```

- Resume: same runner returns, pulls PENDING/in-flight work, continues.
- Permanent runner loss is a user operation (start a new run). No automatic failover or reassignment.

Pipeline rule: once claimed, the workflow must flow continuously — every work reaches COMPLETED or FAILED (report or watchdog). Any stall after assignment is a bug. Pending before assignment is normal waiting, not a stall.

## Recovery

Retry + idempotency throughout. An assigned workflow never returns to the backlog.

```text
unassigned + not yet enqueued  -> reminder enqueues to backlog (idempotent — the single entry)
claim / assignment lost         -> next claim assigns (AssignRunnerAsync idempotent)
work pull lost                  -> runner polls again; workflow re-serves (idempotent)
report lost                     -> runner re-reports; workflow dedups (idempotent)
work pulled, no report          -> watchdog -> FAILED -> advance
process dies mid-work           -> no report -> watchdog -> FAILED; next work waits for runner
process lost (heartbeat)        -> RunnerGrain -> NotifyRunnerLostAsync -> fail in-flight fast; workflow waits for recovery (no re-enter)
```

Before start (no runner): pending is normal; workflow waits in the backlog.
After start (has runner): must progress or fail; the watchdog bounds every work.
