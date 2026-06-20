---
purpose: "Describe workflow scheduling at the grain-interface level."
include:
  - "Grain responsibilities and public grain interfaces."
  - "Discovery, binding, pull delivery, report, supervision, and recovery."
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

Grain-level scheduling. Discovery, binding, delivery, report, supervision, recovery.

## Model

```text
WorkflowGrain
  owns work lifecycle + progression
  serves work on pull; passive between polls
  completion watchdog: no report by work timeout -> work FAILED
  never queries runner state

WorkflowBacklogGrain
  index of workflows with no runner assigned

RunnerGrain
  supervises runner process liveness only (heartbeat, online/offline)
  pulls work from bound workflows on behalf of its process
  does not supervise works

runner process (physical)
  executes work; spawns subprocesses
  enforces execution timeout: kill hung subprocess, report failure
  no supervision duty
```

```text
work truth    = WorkflowGrain
runner truth  = RunnerGrain
backlog truth = WorkflowBacklogGrain
grains cooperate by calls only; no shared state
```

## Interfaces

```text
IWorkflowBacklogGrain
  EnqueueAsync(workflowRunId)
  ClaimAsync(runnerId) -> workflowRunId?

IWorkflowGrain
  AssignRunnerAsync(runnerId) -> Assigned | Rejected   # backlog bind
  PollWork(runnerId) -> WorkDispatch?                   # bound runner pulls work
  ReportResultAsync(runnerId, workId, result)
  NotifyRunnerLostAsync(runnerId)

IRunnerGrain
  RegisterAsync(info)
  HeartbeatAsync()
  PollAsync() -> WorkDispatch?                          # process pulls; grain fetches from bound workflows
  ReportResultAsync(workflowRunId, workId, result)      # relay to workflow
```

Delivery is pull. No push from WorkflowGrain to runner.

## Backlog

Backlog = workflows with no runner assigned. Membership by claim-status only.

```text
enter    : created / resumed / exited approval gate, and has no runner
leave    : claimed by a runner
re-enter : binding released (runner lost)
never    : a bound workflow does not re-enter between work items
```

A bound workflow is served exclusively by its runner; the next work item is pulled directly, not re-queued.

Per-project grain: `WorkflowBacklogKeys.ForProject(projectId)`.

## Project Scan

```text
project-bound runner -> scan its project's backlog
global runner        -> round-robin over known projects (in-memory dir + persisted list)
```

Persisted project ids keep backlog discovery working after server restart.

## Bind

Runner with spare capacity claims an unbound workflow from the backlog.

```text
RunnerGrain            WorkflowBacklogGrain          WorkflowGrain
    |                          |                           |
    | ClaimAsync(runnerId)     |                           |
    |------------------------->|                           |
    |                          | pick unbound workflowRunId|
    |                          | AssignRunnerAsync(runnerId)|
    |                          |--------------------------->|
    |                          |                           | bind runner (1:1, lifetime)
    |                          |                           | remove self from backlog
    |                          | return Assigned           |
    |                          |<--------------------------|
    | return workflowRunId?    |                           |
    |<-------------------------|                           |
    | record bound workflow    |                           |
```

```text
AssignRunnerAsync:
  unassigned + runnable         -> bind runner, Assigned
  already bound to same runner  -> Assigned
  bound to another runner       -> Rejected
  not runnable                  -> Rejected
```

Lazy cleanup: a claim that gets Rejected is dropped from the backlog; the next candidate is tried.

## Pull Work

Bound RunnerGrain pulls work from its bound WorkflowGrain; serves it to its process.

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

No forward call from WorkflowGrain to runner. The bound-workflow set on RunnerGrain is disposable; truth is the WorkflowGrain binding.

## Work State Machine

```text
PENDING --pull--> STARTED --report(success|fail)--> COMPLETED | FAILED
                       |
                       | no report by declared timeout: watchdog -> FAILED
```

- PENDING: work exists, waiting to be pulled. No timeout (waiting for capacity is normal).
- STARTED: pulled by bound runner. Watchdog armed with the work's declared timeout.
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
    |                     |                          | validate bound runner
    |                     |                          | STARTED -> COMPLETED | FAILED
    |                     |                          | advance / arm repair
    |                     | return response          |
    |                     |<-------------------------|
    |<--------------------|
```

Late or duplicate report for an already-terminal work is ignored (idempotent by workId + attempt).

## Binding Lifecycle

One workflow, one runner, for the workflow's life. Never reassigned.

```text
bound              -> stays bound through all work items; must flow continuously
work stalls        -> watchdog fails the WORK (not the binding); workflow advances
runner lost        -> fault: workflow has no executor and cannot proceed until the
                      runner recovers or the run is rerun. Not a legitimate stall.
runner recovers    -> bound runner resumes pulling; in-flight work resumes
```

- Resume: same runner returns, pulls in-flight work, continues.
- Rerun: runner permanently gone; start a fresh run with a new binding. The only way to change runners.

Pipeline rule: once a runner is assigned, the workflow must flow continuously — every work reaches COMPLETED or FAILED (report or watchdog). Any stall after assignment is a bug. Pending before assignment is normal waiting, not a stall. Runner loss after assignment is a fault, resolved by recovery or rerun.

## Recovery

Retry + idempotency throughout.

```text
unbound + has work            -> reminder re-enqueue to backlog (idempotent)
claim / assignment lost       -> next claim re-binds (AssignRunnerAsync idempotent)
work pull lost                -> runner polls again; workflow re-serves (idempotent)
report lost                   -> runner re-reports; workflow dedups (idempotent)
work pulled, no report        -> watchdog -> FAILED -> advance
process dies mid-work         -> no report -> watchdog -> FAILED -> next work waits for runner
process lost (heartbeat)      -> RunnerGrain -> NotifyRunnerLostAsync -> workflow marks waiting
```

Before start (no runner): pending is normal; workflow waits in the backlog.
After start (has runner): must progress or fail; the watchdog bounds every work.
