---
purpose: "Describe workflow scheduling at the grain-interface level."
include:
  - "Grain responsibilities and public grain interfaces."
  - "Workflow scheduling, assignment, delivery, report, and recovery flows."
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

本文记录 workflow 调度相关 grain 的接口级交互。范围只包括 grain 接口和流程，不描述领域模型、数据库表或 UI/API payload。

## Model

```text
WorkflowGrain
  owns assignment + lease
  runs internal RunCoreAsync() to repair scheduling

WorkflowBacklogGrain
  stores workflowRunId candidates

RunnerGrain
  stores pendingWorks + active work only
  polls project backlogs with round-robin fairness
```

`RunCoreAsync` 是 `WorkflowGrain` 内部动作，不暴露为 grain 接口，也不返回 work。

```text
assignment truth = WorkflowGrain
runner cache     = pending/active work
```

## Interfaces

```text
IWorkflowBacklogGrain
  EnqueueAsync(workflowRunId)
  ClaimAsync(runnerId) -> workflowRunId?

IWorkflowGrain
  AssignRunnerAsync(runnerId) -> Assigned | Rejected
  ReportResultAsync(runnerId, workId, result)

IRunnerGrain
  RegisterAsync(info)
  HeartbeatAsync()
  AssignWorkAsync(work) -> Assigned | Rejected
  PollAsync() -> WorkDispatch?
  ReportAsync(workId, result, workflowRunId?)
```

## Project Scan

```text
project-bound runner
    |
    |-- projectId from RunnerInfo
            -> scan only that project's backlog

global runner
    |
    |-- known project ids
    |      |-- in-memory backlog directory
    |      |-- persisted project list
    |
    |-- round-robin project cursor
            -> scan each project backlog fairly
```

```text
Poll #1:  A -> B -> C
Poll #2:  B -> C -> A
Poll #3:  C -> A -> B
```

Global runners should not depend on a single in-memory directory as the only project source. Server restart can lose that directory; persisted project ids keep backlog discovery possible.

## Poll And Assign

```text
RunnerGrain                 WorkflowBacklogGrain              WorkflowGrain
    |                                |                               |
    | PollAsync()                    |                               |
    | drain pendingWorks             |                               |
    | cleanup stale local work        |                               |
    |                                |                               |
    | if capacity full               |                               |
    |     return null                |                               |
    |                                |                               |
    | ClaimAsync(runnerId)           |                               |
    |------------------------------->|                               |
    |                                | pick workflowRunId            |
    |                                | AssignRunnerAsync(runnerId)   |
    |                                |------------------------------>|
    |                                |                               | assign/restore runner
    |                                |                               | RunCoreAsync()
    |                                |                               | if work needs delivery
    | AssignWorkAsync(work)          |                               |
    |<---------------------------------------------------------------|
    | add pendingWorks               |                               |
    | return Assigned                |                               |
    |---------------------------------------------------------------->|
    |                                | return Assigned               |
    |                                |<------------------------------|
    |                                | remove candidate              |
    | return workflowRunId?          |                               |
    |<-------------------------------|                               |
    | drain pendingWorks             |                               |
    | return WorkDispatch?           |                               |
```

`AssignRunnerAsync` is idempotent:

```text
unassigned + runnable              -> assign runner, RunCoreAsync(), Assigned
already assigned to same runner    -> RunCoreAsync(), Assigned
assigned to another runner         -> Rejected
not runnable / missing             -> Rejected
```

Backlog behavior:

```text
Assigned -> remove candidate, return workflowRunId
Rejected -> remove candidate, scan next
```

## Run Core

```text
WorkflowGrain.RunCoreAsync()
    |
    |-- no runner assigned + needs runner
    |       -> backlog.EnqueueAsync(workflowRunId)
    |
    |-- runner assigned + work exists + no lease
    |       -> create lease
    |       -> runner.AssignWorkAsync(work)
    |
    |-- runner assigned + lease exists
    |       -> runner.AssignWorkAsync(leased work)
    |
    |-- no work / already consistent
            -> no-op
```

`RunCoreAsync` is a side-effecting repair step:

```text
input:  current workflow grain state
output: persisted scheduling side effects
return: nothing meaningful to callers
```

## Runner Work Cache

Runner does not store assigned workflows.

```text
WorkflowGrain assignment
    |
    |-- authoritative
    |-- durable
    |-- survives runner/server restart

RunnerGrain work cache
    |
    |-- pendingWorks
    |-- active work tracking
    |-- disposable
```

```text
RunnerGrain.AssignWorkAsync(work)
    |
    |-- same workflowRunId + workId already pending/active
    |       -> Assigned
    |
    |-- same workflowRunId has different pending/active work
    |       -> Rejected(workflow-busy)
    |
    |-- runner offline / cannot accept
    |       -> Rejected
    |
    |-- otherwise
            -> add to pendingWorks
            -> Assigned
```

`pendingWorks` 是可丢的投递缓存；事实来源是 `WorkflowGrain` 的 assignment + lease。

Capacity is based on pending/active work, not assigned workflow count.

```text
can accept work when:
  activeWorkflowCount < maxWorkflowSlots

activeWorkflowCount =
  distinct workflowRunId in pending/active work
```

Before polling backlog or reporting runtime state, runner validates its local active cache against `WorkflowGrain`:

```text
local active work
    |
    |-- owner is this runner?
    |-- workflow is Running?
    |-- current workId still matches?
    |
    |-- no  -> remove from local cache
    |-- yes -> keep as active
```

## Report

```text
RunnerProcess              RunnerGrain                    WorkflowGrain
    |                           |                                |
    | PollAsync()               |                                |
    |-------------------------->|                                |
    |                           | pending -> active              |
    | return WorkDispatch       |                                |
    |<--------------------------|                                |
    |                           |                                |
    | execute work              |                                |
    |                           |                                |
    | ReportAsync(workId,result,workflowRunId?)                  |
    |-------------------------->| ReportResultAsync(...)         |
    |                           |------------------------------->|
    |                           |                                | validate lease owner
    |                           |                                | advance workflow
    |                           | return                         |
    |<--------------------------|<-------------------------------|
```

`workflowRunId` is optional for compatibility, but callers should provide it when they have a `WorkDispatch`. It makes report routing unambiguous when one runner holds multiple workflows with similar work ids.

## Recovery Reminder

```text
WorkflowGrain reminder
    |
    |-- no runner attention needed
    |       -> no-op
    |
    |-- unassigned + needs runner
    |       -> backlog.EnqueueAsync(workflowRunId)
    |
    |-- assigned + needs runner
            -> RunCoreAsync()
            -> if runner cannot accept: keep assignment + lease
```

Strong assignment rule:

```text
runner offline / timeout / unregister
  does not release assignment
  does not assign workflow to another runner
  workflow keeps retrying the same runner by reminder
```

Stop/cancel does not erase assignment. It clears executable state; assignment remains available for inspection.

## Recovery Cases

```text
assignment response lost
  -> reminder EnqueueAsync(workflowRunId)
  -> same runner assignment is idempotent

work dispatch lost
  -> reminder RunCoreAsync()
  -> same leased work is assigned to runner again

runner pending buffer lost
  -> reminder RunCoreAsync()
  -> same leased work is assigned to runner again
```

## Lazy Backlog Cleanup

```text
RunnerGrain              WorkflowBacklogGrain          WorkflowGrain
    |                             |                           |
    | ClaimAsync(runnerId)        |                           |
    |---------------------------->|                           |
    |                             | AssignRunnerAsync(...)    |
    |                             |-------------------------->|
    |                             |                           | Rejected
    |                             |<--------------------------|
    |                             | remove candidate          |
    |                             | scan next                 |
```
