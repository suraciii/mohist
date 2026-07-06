---
purpose: "Describe workflow scheduling at the grain-interface level."
include:
  - "Ownership model: which party holds which scheduling fact."
  - "Poll reconciliation: desired − reported diff dispatch."
  - "Claim as a single write; fairness via ReadySince round-robin."
  - "Report ack semantics (at-least-once), supervision, recovery."
exclude:
  - "WorkflowRun/domain model internals; keep them in the Domain Model chapter only."
  - "Database schemas, persistence implementation, migrations, and storage tables."
  - "HTTP API payloads, Web UI behavior, and user-facing copy."
  - "Cancelling in-flight work — the system does not provide it."
style:
  - "Prefer diagrams over prose."
  - "Keep text short and human-readable."
  - "Use workflowRunId for identifiers in interaction diagrams."
---

# Workflow Scheduling

Level-triggered reconciliation. The scheduler keeps no memory of its own:
every decision — dispatch, re-dispatch, fairness, closeout — is a stateless
query over persisted state, repaired by the next poll.

## Model

```text
WorkflowGrain / WorkflowRun          ★ the single dispatch ledger
  owns Assignment + work lifecycle (Pending / Running / terminal)
  ClaimNext: one atomic write Pending→Running (acquires stage lock)
  consumes reports; idempotent (terminal work re-report answers stale)
  no timer, no runner concept — never queries runner state

AgentJobGrain
  owns its work state + DispatchSnapshot (no run to re-render from,
  so the snapshot lives on the owner)

RunnerGrain
  presence: lastSeen — poll IS the heartbeat → online/offline
  slots: capacity configuration (control-plane owned)
  closeout: on presence loss, synthesize FAILED for the runner's
            Running works via the normal report channel
  holds NO work records

DispatchService (stateless, not a grain)
  per poll computes desired − reported and renders dispatches
  from persisted state; keeps no cursor, no cache, no ledger

runner process (physical)
  executes works concurrently; owns execution timeout (progress-aware)
  reports its full level state on every poll: inFlight + awaitingAck
  retries reports with backoff until acked
```

Every scheduling fact has exactly one persistent owner:

```text
who was dispatched what, and how far   → WorkflowRun / AgentJob (store-queryable)
what is actually executing right now   → runner process memory (fully reported each poll)
is the runner alive                    → RunnerGrain.lastSeen
```

There is no third copy in the middle. A dispatch is always re-renderable
from the persisted run (`WorkItem` and its envelope are pure functions of
the run), so no dispatch snapshot exists for workflow work.

Core invariants:

```text
the workflow run IS the dispatch ledger
Running ⟹ reconciled within one poll period:
  reported by its runner  ∨  re-dispatched  ∨  closed out on presence loss
|Running works assigned to a runner| ≤ slots   (enforced at claim)
```

## Interfaces

```text
IWorkflowGrain
  AssignRunnerAsync(runnerId) -> Assigned | Rejected   # idempotent arbiter; sets Assignment
  ClaimNextAsync(runnerId)    -> WorkItem | null       # single write: pick NextWork,
                                                       # acquire stage lock, mark Running, persist
  ReportTaskOutcomeAsync(runnerId, workId, outcome)  -> Accepted | Stale
  ReportCheckOutcomeAsync(runnerId, workId, outcome) -> Accepted | Stale

IRunnerGrain
  RegisterAsync(info)          # first contact / info repair
  slots get/update             # capacity config
  presence: touched by poll; persistent-reminder expiry -> offline + closeout
```

```text
POST poll
  request  { inFlight:    [workKey...],     # executing now
             awaitingAck: [workKey...] }    # finished, result not yet acked
  response { dispatches:  [WorkDispatch...] }

workKey = ownerKind:ownerId:workId
```

`workKey` is split on the first and last `:` so that `workId` may itself
contain `:` (e.g. `recover:rebase.1`). `ownerId` is the workflow run id
(workflow) or agent job id (agent-job).

Delivery is pull. Discovery is store queries, not grain calls. There is no
push from WorkflowGrain to runner, and no relay grain between the process
and the owner of a work.

## Poll Reconciliation

One poll = one reconciliation round. First dispatch, recovery after process
restart, and lost-response repair are all the same path: `desired − reported`.

```text
runner process               DispatchService                     store / grains
    |                             |                                   |
    | POST poll                   |                                   |
    | { inFlight, awaitingAck }   |                                   |
    |---------------------------->|                                   |
    |                             | ① touch presence (poll is heartbeat)
    |                             |                                   |
    |                             | ② desired ← Running works of runs |
    |                             |    WHERE assigned = me            |
    |                             |    (+ agent-job works assigned=me)|
    |                             |                                   |
    |                             | ③ repair = desired − reported     |
    |                             |    render each from persisted run |
    |                             |    (agent-job: owner snapshot)    |
    |                             |                                   |
    |                             | ④ spare = slots − |desired|       |
    |                             |    while spare > 0:               |
    |                             |      Ready runs assigned to me    |
    |                             |      ORDER BY ReadySince ASC      |  ← fairness
    |                             |      ClaimNextAsync(me) ------------>| Pending→Running
    |                             |        ok   -> render, spare--    |   + stage lock
    |                             |        null -> next candidate     |
    |                             |    still spare:                   |
    |                             |      claimable Pending runs       |
    |                             |      → AssignRunner → ClaimNext   |
    |                             |                                   |
    | { dispatches[] }            |                                   |
    |<----------------------------|                                   |
    | inFlight.set(key) per dispatch (synchronously, before next poll)|
    | execute concurrently        |                                   |
```

Ordering within a poll: repair first (debts already owed), then serve
already-assigned Ready runs, then claim new workflows — held work always
precedes expansion.

`reported − desired` (the run was stopped or advanced past the work while
the process executes it) triggers **no action**: the system does not cancel
in-flight work. The process runs it to completion; the eventual report is
answered `Stale` — which is an ack — and the result is discarded
idempotently.

Race-freedom of `desired − reported`: the process adds a work to its
in-flight map synchronously between receiving a dispatch and its next poll,
so a freshly delivered dispatch can never be mistaken for a loss. The only
way a Running work is absent from the report is that the process never had
it or lost it — both want a re-dispatch.

**Implementation constraint — the reported set is process-lifetime state.**
The process's reported set (`inFlight ∪ awaitingAck`) must survive poll
exceptions and connection resets. A poll that throws must not discard works
still executing or awaiting ack. If the reported set were scoped to the poll
loop (e.g. a method-local map abandoned when the poll call rejects), then
any transient poll failure would make every held work vanish from the
report and be re-dispatched — a rollback storm that duplicates execution
and eventually fails works as `runner-lost`. The reported set belongs to
the process, not to a single poll attempt.

## Claim

`ClaimNextAsync` is the only write that starts work: it picks the run's
next pending work, acquires the sequential stage lock, marks the work
Running with the runner identity, and persists — one atomic transition on
the single-writer grain. There is no offer phase and no runner-side
pre-registration, because there is no runner-side record whose existence
would need guaranteeing.

```text
PENDING --ClaimNext--> RUNNING --report(success|fail)--> COMPLETED | FAILED
```

A claim that fails (stage lock contended, state moved on) returns null;
the DispatchService tries the next candidate this poll and the run is
retried on later polls. A claim that succeeds but whose dispatch never
reaches the process needs no handling: the work is Running and unreported,
so the next poll re-dispatches it.

## Fairness — round-robin from ReadySince

A run records when it (re-)entered Ready (`ReadySince`, a persisted status
transition timestamp). Serving Ready runs in `ReadySince ASC` order yields
round-robin with zero scheduler state:

```text
work completes → run advances → next work pending → run back to Ready
                                                    ReadySince := now
just-served runs re-queue at the tail; the longest-waiting run is at the head
```

Example — 2 slots, runs A, B, C all with work, D gated:

```text
t0  desired={}          queue=[A,B,C]  spare=2  → claim A.w1, B.w1
t1  A.w1 done, A ready  queue=[C,A]    spare=1  → claim C.w1
t2  B.w1 done, B ready  queue=[A,B]    spare=1  → claim A.w2
t3  C.w1 done, C ready  queue=[B,C]    spare=1  → claim B.w2      … stable rotation
```

Properties: fairness is a property of persisted data (survives server
restart, needs no cursor); rotation granularity is one work (a run has at
most one dispatchable work at a time); gated/idle runs are not Ready and
cost nothing. The ordering clause is the pluggable policy point (e.g.
`Priority DESC, ReadySince ASC`); default is pure FIFO. Claimable
discovery keeps `CreatedAt ASC` with jitter against thundering herds.

## Capacity

`slots` bounds **concurrently executing workflow works**, not held
assignments. A runner may hold many idle/gated assigned workflows while
executing up to `slots` works. The gate is evaluated at claim time from the
store (`|Running assigned to me| < slots`); the process enforces nothing
and executes whatever it is handed.

## Assignment

Unchanged in substance. One workflow, one runner, sticky through idle,
gates, and work boundaries. Claimable is a data property, not a queue:

```text
WorkflowRuns WHERE Assignment IS NULL AND Status = Pending   [AND ProjectId = @p]
```

`AssignRunnerAsync` is the idempotent arbiter (unassigned+runnable → set;
same runner → Assigned; other runner → Rejected; not runnable → Rejected).
Optimistic claiming: concurrent runners may race a candidate; the arbiter
admits one.

Release is not automatic. A workflow assigned to a permanently lost runner
is unblocked by an explicit operator reassign (tracked separately); on
release its Running works are closed out and the run returns to claimable.

## Report

Reports flow directly to the owning grain; translation is a stateless
service; no relay, no runner-side bookkeeping.

```text
runner process            api route               owner grain
    | report result           |                       |
    |------------------------>| translate (stateless) |
    |                         | ReportOutcome -------->| idempotent by workId
    |      Accepted | Stale   |<-----------------------|
    |<------------------------|   both are acks
    | awaitingAck.remove(key) |
```

At-least-once: a transport failure never rewrites a result. The process
moves a finished work to `awaitingAck`, retries the original result with
backoff, and keeps the work in its poll report meanwhile — so it is never
mistaken for lost and re-dispatched. `Accepted` and `Stale` both terminate
the retry.

Report producers are indistinguishable to the owner: the executing process
(normal completion or its own timeout failure) or RunnerGrain closeout.
WorkflowGrain never learns why a work failed — only that it did.

## Supervision

Two levels, one rule each. No server-side work-completion wall clock.

```text
work wedged / runaway    runner process   progress-aware timeout: kill, report FAILED
runner gone              RunnerGrain      poll-freshness expiry (persistent reminder)
                                          → offline → closeout: query the runner's
                                          Running works, synthesize FAILED("runner-lost")
(work timeout)           server           none — a work reported in-flight is alive;
                                          only the process judges slow
```

The HTTP heartbeat endpoint degrades to an info-refresh channel
(capabilities, models, buildGitHash); poll freshness is the presence
signal. The registry is written only on state or info change, never per
poll. SignalR liveness probing serves the push transport only and takes no
part in presence.

## Recovery

Two paths, no per-failure rules:

```text
every lost message        → repaired by the next poll's diff
runner permanently gone   → presence expiry → closeout
```

| failure | recovery |
|---|---|
| dispatch response lost | next poll: desired − reported → re-dispatch |
| process restart (memory gone) | same — empty report → full re-dispatch |
| render fails after claim | same — retried every poll |
| report transport fails | awaitingAck retry; still reported, never re-dispatched |
| duplicate / late report | owner idempotent → Stale (an ack) |
| work wedged | process timeout → FAILED |
| runner lost | closeout synthesizes FAILED |
| runner returns after closeout | its reports answer Stale; its works are no longer desired and simply drain |
| run stopped while work executes | no cancellation; work drains, report answers Stale |

Old clients that poll without a body report nothing; reconciliation is
skipped for them and presence-loss closeout remains the only safety net.

## 实装差距

正文描述目标设计（对账模型）。当前实现是前一代边沿触发协议，差距由 epic #44「调度链路设计收敛」跟踪推进。

**现状机制（将被目标取代）**：

- offer/claim 两阶段（`PollWorkAsync` + `ClaimAsync`）+ runner 侧 claim 前预登记与回滚 → 目标合并为 `ClaimNextAsync` 单次写。
- RunnerGrain 双台账（grain persisted `_worksState` + `RunnerWorkStore` ledger）与 `WorkItemSnapshot`/dispatch 快照，以及 Hydrate / Reconfirm / orphan-drop 恢复分支 → 目标删除，run 即台账、dispatch 从 run 重渲染（现有降级路径 `RecoverWorkItemFromRun` 转正）。
- 丢失恢复走 `RollbackLostWorkAsync`（回退 Pending）+ `DequeuePendingWorkAsync`（重派队列）双路径 → 目标统一为 `desired − reported` diff。
- report 经 RunnerGrain 中继并记账；传输失败会被降级为 `failed` 业务结果，且 work 过早移出 in-flight 汇报（重复执行风险） → 目标 report 直达属主、awaitingAck at-least-once（issue #393）。
- 服务端 `WorkCompletionTimeout`（30 分钟墙钟 reminder）合成失败 → 目标删除，work 级 liveness 即 poll 汇报，兜底只剩 presence 关账。
- presence 由 HTTP heartbeat + 每次 poll `TouchPresenceAsync`（每 poll 空写全局 registry）双通道维持 → 目标 poll 即心跳、registry 仅写变更。
- 轮转公平性缺失：assigned 发现按 `WorkflowRunId` 字典序 → 目标 `ReadySince ASC`（需要状态迁移时间戳列）。
- 每 poll 至多一个 dispatch → 目标一次 poll 可携带补派 + 新 claim 多个 dispatch。

**保持不变、无差距**：pull-only 交付、Assignment 唯一真相与 sticky 语义、claimable 的数据性质、stage lock 在 claim 时获取、WorkflowGrain 无 timer 不感知 runner、监督分层（进程管进度、控制平面只管存亡）。

**相邻文档**：`design/runner.md` 的聚合描述（`assignedWorkflows` 事件增量集合与 `Claim/Release` 行为）基于旧模型，随本 spec 落地一并修订；显式 reassign 出口由 issue #395 单独跟踪。
