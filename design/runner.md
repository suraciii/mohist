# Runner

## Structure

Fields grouped by update lifecycle. Never by "who reported".

| Lifecycle | Trigger | Change | Invalidate on |
|---|---|---|---|
| persistent | control plane | rare, single-field | never |
| event-increment | agent-job push/report | add/remove | runner offline |
| snapshot-replace | each poll / register-unregister | overwrite | next poll |

```
Runner
  runnerId                       identity
  slots                          persistent; control plane owns
  lastSeen                       snapshot; poll = heartbeat; timeout = offline
  info: RunnerInfo|null          register fills it; heartbeat-repair refreshes; unregister clears
  agentJobWorks: [RunnerWork]    event-increment; push ledger for agent jobs (no run to rerender)
```

No workflow work ledger. Workflow work truth lives in the run (store: `WHERE Status=Running AND AssignedRunnerId=R`). Slot invariant (`|running| ≤ slots`) checked at claim time from store, not maintained here.

```
RunnerInfo
  state: online|offline          derived from lastSeen freshness
  hostname, buildGitHash
  capabilities, coderModels, coderModelVariants
```

## Why no workflow work ledger

| Check | Result |
|---|---|
| guards a Runner invariant? | no |
| not derivable from other aggregates? | derivable: `store.Where(Running, AssignedRunnerId=R)` |
| needed by behavior signatures? | no behavior takes workflow work as param |

## Behaviors

```
Register(info)                  state=online, fills info, writes registry
Unregister()                    state=offline, clears info & agentJobWorks,
                                closeout → FAILED("runner-lost") to owners
TouchPresence()                 lastSeen=now; no registry write
HeartbeatRepair(info)           refreshes info; writes registry
AssignAgentJob(work)            agentJobWorks.add
DequeueAssignedAgentJob()       next pending → Running
ReportAgentJobResult(id,w,r)    agentJobWorks.remove → AgentJobGrain
Update(slots)                   write-through
```

Each behavior touches exactly one group.

## Runtime read

`GetRuntimeStateAsync()` → `RunnerRuntimeState` (status + lastSeen + activeWorks).

activeWorks merged from:
- workflow: store query `Running assigned to me`, per-run current task/checks
- agent-job: this aggregate `agentJobWorks` (Pending/Running)

## Transport

| What | How |
|---|---|
| workflow work | pull-only; DispatchService per poll; report to owner grain |
| agent-job work | push; AssignAgentJob places it; poll dequeues |
| presence | poll = heartbeat (TouchPresence) |
| info | register/unregister/heartbeat-repair; never per-poll |
