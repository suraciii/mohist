# Runner

## Structure

Fields grouped by update lifecycle. Never by "who reported".

| Lifecycle | Trigger | Change | Invalidate on |
|---|---|---|---|
| persistent | control plane | rare, single-field | never |
| event-increment | agent-job push/report | add/remove | runner offline |
| snapshot-replace | register / successful poll / unregister | overwrite | next successful poll |

```
Runner
  runnerId                       identity
  slots                          persistent; control plane owns
  lastSeen                       snapshot; register establishes, successful poll renews
  info: RunnerInfo|null          register fills it; heartbeat-repair refreshes; unregister clears
  agentJobWorks: [RunnerWork]    event-increment; push ledger for agent jobs (no run to rerender)
```

No workflow work ledger. Workflow work truth lives in the run (store: `WHERE Status=Running AND AssignedRunnerId=R`). Slot invariant (`|running| ≤ slots`) checked at claim time from store, not maintained here.

```
RunnerInfo
  state: online|offline          register establishes; successful-poll freshness maintains
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
Register(info)                  state=online, lastSeen=now, fills info, writes registry
Unregister()                    state=offline, clears info & agentJobWorks,
                                closeout → FAILED("runner-lost") to owners
TouchPresence()                 successful poll: lastSeen=now; restores online registry state
HeartbeatRepair(info)           refreshes info only; never refreshes presence
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

## Runner process contract

The runner process has one process-critical reconciliation loop. It owns poll cadence and bounded retries for unacknowledged reports. Transport failures do not end the loop. If the loop exits unexpectedly, the process exits; auxiliary heartbeat or SignalR loops must not keep a non-polling runner alive.

The reported set (`inFlight ∪ awaitingAck`) belongs to the process lifetime and survives connection recovery. A work lost with the runner is reported to its owner as `FAILED("runner-lost")`. The owner decides the WorkflowRun transition; Runner has no `Interrupted` workflow status.
