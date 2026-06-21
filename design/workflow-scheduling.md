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
enter    : new run created, has no runner        (once per run)
leave    : claimed by a runner
re-enter : never
```

A bound workflow is held by its runner for the whole run — through idle, approval gates, and work-item boundaries. The runner polls by state (busy → return work; idle/gated → null) and never releases the workflow back. Capacity gates concurrent execution (active works), not bound-workflow count: a runner may hold many idle bound workflows while executing up to its slot count.

Per-project grain: `WorkflowBacklogKeys.ForProject(projectId)`.

## Project Scan

```text
project-bound runner -> scan its project's backlog
global runner        -> round-robin over known projects (in-memory dir + persisted list)
```

Persisted project ids keep backlog discovery working after server restart.

## Bind

Runner with spare capacity claims an unbound workflow from the backlog. Write order is **record-then-bind** so the "bound but runner forgot" state is structurally impossible.

```text
RunnerGrain            WorkflowBacklogGrain          WorkflowGrain
    | ClaimAsync(runnerId)     |                           |
    |------------------------->|                           |
    |                          | pick unbound workflowRunId|
    |                          | (optimistic remove)       |
    | return workflowRunId?    |                           |
    |<-------------------------|                           |
    | record wfId TENTATIVE (persist)   ← first durable write
    |                                                      |
    | AssignRunnerAsync(runnerId)             ← second durable write (truth)
    |----------------------------------------------------->|
    |                                                      | bind (1:1, lifetime)
    | return Assigned | Rejected                           |
    |<-----------------------------------------------------|
    | Assigned -> mark CONFIRMED (persist)                 |
    | Rejected  -> drop wfId                               |
```

```text
AssignRunnerAsync (idempotent arbiter; truth = WorkflowGrain binding):
  unassigned + runnable         -> bind runner, Assigned
  already bound to same runner  -> Assigned
  bound to another runner       -> Rejected
  not runnable                  -> Rejected
```

Lazy cleanup: a Rejected claim's candidate is dropped from the backlog; the next candidate is tried.

### Eventual consistency (boundList ↔ binding)

The three writes (backlog optimistic-remove, Runner record, Workflow bind) cannot be atomic across grains. Converge via write-order + idempotency — no forward call from WorkflowGrain:

- **record-then-bind**: Runner records the candidate BEFORE binding, so "bound but runner forgot" never happens.
- **Activation reconfirm**: on Runner activation, re-run `AssignRunnerAsync` for any TENTATIVE entries (crash between record and bind) — idempotent → Assigned (confirm) or Rejected (drop).
- **Orphan re-enqueue**: if backlog optimistically removed a candidate but no one bound it (runner crashed before record), the workflow's reminder re-enqueues (unbound + not in backlog).
- **Stale cleanup (direction 2)**: a stale RunnerGrain entry (has W, W not bound to it) → activation reconfirm Rejected, or `PollWork` validation rejects → Runner drops W.

Result: boundList and binding are eventually consistent in both directions.

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

One workflow, one runner, for the run's life. Sticky: never released, never reassigned.

```text
bound              -> stays bound through work items, idle periods, and gates; must flow continuously
work stalls        -> watchdog fails the WORK (not the binding); workflow advances
runner transient loss (process restart) -> grain survives; bound runner resumes pulling on recovery (in-flight failed fast, re-pulled)
runner permanently gone -> out of scope: user starts a fresh run (new backlog entry, new binding)
```

- Resume: same runner returns, pulls PENDING/in-flight work, continues.
- Permanent runner loss is a user operation (start a new run). No automatic failover or reassignment.

Pipeline rule: once claimed, the workflow must flow continuously — every work reaches COMPLETED or FAILED (report or watchdog). Any stall after assignment is a bug. Pending before assignment is normal waiting, not a stall.

## Recovery

Retry + idempotency throughout. A bound workflow never returns to the backlog.

```text
unbound + not yet enqueued   -> reminder enqueues to backlog (idempotent — the single entry)
claim / assignment lost       -> next claim binds (AssignRunnerAsync idempotent)
work pull lost                -> runner polls again; workflow re-serves (idempotent)
report lost                   -> runner re-reports; workflow dedups (idempotent)
work pulled, no report        -> watchdog -> FAILED -> advance
process dies mid-work         -> no report -> watchdog -> FAILED; next work waits for runner
process lost (heartbeat)      -> RunnerGrain -> NotifyRunnerLostAsync -> fail in-flight fast; workflow waits for recovery (no re-enter)
```

Before start (no runner): pending is normal; workflow waits in the backlog.
After start (has runner): must progress or fail; the watchdog bounds every work.
