# Self Review — Issue 567 (Runner 更新应中断并恢复活跃 Agent 工作)

Reviewed: `proposal.md`, `design.md`, `tasks.json`, `recovery.md`, and all five
`specs/` files, judged against the issue body (goals + four acceptance criteria)
*before* re-reading the plan's own framing. Every factual claim about the
current codebase was verified against live source (see "Verified codebase
claims" below). This is a first review: full sweep.

## Verdict

**FAIL** — one must-fix problem: the receipt protocol's triggering input (the
update-operation identity and the update-interrupt fact) has no specified path
from the Server to the Runner process.

## Must-fix findings

### MF-1: No specified Server→Runner channel for the update-operation identity / update-interrupt fact

The plan's central mechanism is the `update-interrupted` receipt: the spec
(`runtime-agent-recovery-receipt`) requires its payload to "name the update
operation", D2 requires the `interrupted` journal entry to carry
`updateOperationId`, and D3 has `RunnerHost` write receipts "referencing the
update operation" at shutdown. Nothing in the design, tasks, or specs specifies
how the Runner process obtains that identity — or how it learns that a given
SIGTERM shutdown is update-caused at all (versus an ordinary service restart,
which must not produce receipts).

The plan's only statement is T-003's note: *"The runner learns the update
operation id from the T-001 confirmation response."* That response is the HTTP
`RunnerUpdateInterruptResponse` returned by `POST /api/runner/{id}/update-interrupt`
to the **CLI** (`RunnerRefreshVerifier.InterruptRunnerAsync`,
`UpdateOperations.UpdateRunnerAsync`) — the Runner is never a party to that
call. Verified in source: the Runner today has no awareness of the server-side
drain/interrupt (the poll response carries dispatches only; no drain/interrupt
knowledge exists anywhere under `packages/runner/src`), and the plan adds none —
no SignalR push, poll-response field, or shutdown-time fetch is specified, even
though suitable channels exist (`RunnerSessionStopDelivery`,
`RunnerSessionCommandDispatcher`, the poll contract, or a bounded shutdown-time
query).

**Why this is must-fix:** without the handoff, T-003 cannot emit a valid
`update-interrupted` receipt, so T-004/T-005 replacement arbitration never
fires for any work, and every managed update ends with all affected work
unresolved via the no-receipt settlement-deadline backstop. That defeats the
issue's core goal — "Runner 重连后，系统应依据持久化的工作事实恢复或重新投递这些工作"
(work recovered or re-dispatched from persisted facts after reconnect) — and
acceptance criterion 3 ("重连和 reconciliation 后工作能够继续执行或进入明确终态"):
work would only ever reach the *terminal* half of that criterion, never the
*continue-executing* half, in the normal path. The plan is therefore incomplete
on a load-bearing mechanism, and its one pointer to a mechanism is factually
wrong about the call topology. The fix is to specify the channel (e.g. a SignalR
notification at fence creation, a poll-response field, or a bounded
shutdown-time fetch of pending update operations for the runner's identity),
what payload it carries (operation id and, ideally, the affected-work inventory
— see Obs-4), and the no-operation-known behavior (no receipt, `started` fence
stands, per the honesty rule).

## Per-dimension verdicts (first review, full sweep)

### 1. Issue goals & acceptance criteria — checked, no issue in mapping

Re-read the issue first. Goal→plan mapping is complete and explicit:

- 停止接收新工作 → admission closes at confirmation (landed `_draining` +
  `runner-update-work-interruption` spec; server refuses claims/dispatch to the
  draining Runner).
- 立即转入可恢复中断状态 → durable `RunnerUpdateOperation` fence at
  confirmation, synchronous per-owner marking, explicitly *not* via disconnect
  observation or settlement timeout (D1, T-001, spec scenarios).
- 尽快终止旧 Runner、启动新版本 → bounded cooperative stop; restart proceeds
  when the budget expires; never waits for natural turn completion (D3, T-003,
  "prompt stop" requirement + scenarios).
- 重连后依据持久化事实恢复/重投递、保持身份与幂等 → receipts with frozen
  binding, at-most-once arbitration, replacement delivery identity, journal
  fencing, server-side redelivery suppression (T-002/T-003/T-004/T-006).
- 用户看到中断中/恢复中/已恢复 → interrupting/interrupted → recovering →
  recovered lifecycle in read models, DTOs, web (`agent-work-interruption-visibility`,
  T-007).
- 不再出现 `session.abort fetch failed` 式错误 → actionable stop-failure
  states carrying update context (visibility spec, requirement 2).
- 无法确认恢复时 CLI 明确报告、不宣称成功 → bounded recovery wait, per-work
  recovered/unresolved listing, non-success exit (`runner-update-recovery-reporting`,
  T-008).

All four acceptance criteria are addressed (AC4's deterministic-test demand is
an explicit per-task acceptance criterion, incl. the full
active→interrupt→restart→reconnect→resume sequence and duplicate-delivery
idempotence in T-006).

### 2. Coverage — checked, no issue

Every goal and acceptance criterion has a corresponding spec requirement,
design decision, and task. No goal is left to implicit coverage. (MF-1 is a
*mechanism* gap inside covered goals, not a missing goal; it is counted under
correctness.)

### 3. Correctness — one must-fix (MF-1); otherwise checked, no issue

Adversarially probed the design's failure cases; each holds up:

- Crash mid-marking: operation record persisted first, idempotent marking,
  confirmation only after all markings commit (D1 risk item, T-001 AC5). Sound.
- Unconfirmed interrupt changes nothing: matches the landed admission fence
  (verified `ManagedRunnerInterruptSpecs`, `RunnerUpdateInterruptSpecs`).
- Stop-unconfirmed / budget-expired / runtime-unreachable: no receipt, `started`
  fence stands, work reported unresolved — honest-failure invariant is stated
  consistently across design, specs, and T-003; cannot be manufactured from
  idle observation or transcript text. Sound.
- Duplicate receipts / redelivery: exact-duplicate no-op with same ack; journal
  `begin` fence refuses replay per identity; `HasUnresolvedAgentResult()`
  suppression ends only when the replacement settles; frozen original binding
  blocks late old-turn settlement. Sound and consistent with the existing
  `WorkResultJournal` and `AgentResultSettlement` semantics.
- First rollout with pre-change old Runner: no receipts → fence stands, legacy
  `agent-result-unconfirmed` backstop, safe degradation. Sound.
- The one mechanism that cannot be assembled from the plan as written is the
  Runner's knowledge of the operation id / update-caused shutdown → MF-1.

### 4. Consistency with the current codebase — checked, no issue (MF-1's wrong
pointer noted there)

Verified codebase claims (all accurate):

- `RunnerGrain.BeginUpdateInterruptAsync()` exists (drain + runtime-state
  snapshot, `RunnerGrain.cs:338`); `POST /api/runner/{id}/update-interrupt`
  exists with exactly the described response shape (`RunnerRoutes.cs:98`), and
  is grain-memory-only today — the gap the plan describes is real.
- CLI admission fence landed: `UpdateOperations.UpdateRunnerAsync` refuses
  restart on unconfirmed interrupt; `ManagedRuntimeTransaction`,
  `RunnerRefreshVerifier`, `UpdateOutcomeReporter`, `RunnerRefreshOutcome` all
  exist as named.
- `WorkResultJournal` (`started`/`completed`, retire-on-durable-ack,
  `.mohist/runner-state/`) and `RunnerHost.executeAndTransition` persist-before-
  first-report match the "already landed" claims; issue-570 is indeed the landed
  returned-result slice.
- `AgentResultSettlement` carries exactly the frozen binding fields listed
  (TaskRunId/WorkId/RunnerId/AgentSessionId/AgentTurnId/Runtime/RuntimeSessionId)
  with `AwaitingResult → Unknown → Blocked`; `WorkflowRun.HasUnresolvedAgentResult()`
  suppresses redelivery; `WorkflowWorkLifecycle.ClaimWorkAsync` allocates work
  identities; `AgentJobGrain` + `AgentJobStatus.Unknown` exist; `DispatchService`
  redelivery/suppression and poll-reported work keys behave as T-006 assumes.
- Pi adapter's `session.abort()` + `isStreaming` watch → first-class
  `stopConfirmed` exists (`pi/runtime.ts` cancel); OpenCode lifecycle surface
  exists. The `session.abort fetch failed` symptom maps to the abort-transport
  failure path the visibility spec targets.
- Spec format (`### Requirement` / `#### Scenario` WHEN-THEN), spec-test
  conventions, deterministic test patterns (fakes, injectable time), and web
  vitest + playwright browser tests all match repo conventions; task spec
  anchors all resolve to real requirement headers.

The single inconsistency: T-003's note points the Runner at a response only
the CLI receives (folded into MF-1).

### 5. Task breakdown — checked, no issue

Eight tasks, dependency graph acyclic and sensible (T-006 correctly waits on
runner receipts + both arbitration tasks; T-008 owns the full `npm run verify`
gate). Every task has concrete, verifiable acceptance criteria including
deterministic test requirements and the repo's real test gates. Slice order
(durable fence → receipt port → runner journal → arbitration → resumption →
visibility/reporting) is independently shippable as claimed, and T-002's
interim "retryable response" for fence-matching receipts composes correctly
with T-003-before-T-004 ordering.

## Observations (non-blocking)

1. **Abandoned confirmed interrupt.** If the interrupt is confirmed but the
   restart then fails (or the operator aborts), admission stays draining
   indefinitely — `CancelDrainAsync` exists on `IRunnerGrain` but no caller
   invokes it — and marked work resolves only via the settlement-deadline
   backstop. Pre-existing behavior carried forward; safe, but the
   update-operation record might want an explicit abandoned/cancelled
   disposition for the read model.
2. **Non-Agent active work** (script/check tasks) interrupted by an update is
   out of scope, matching the issue's Agent-work framing — but such work also
   ends behind a `started` fence with the same unresolved consequences. Worth
   a sentence in the proposal's non-goals; not required by this issue.
3. **Terminal-result receipt channel ambiguity.** The spec supports reading
   `terminal-result` receipts as riding either the new `/recovery-receipt`
   endpoint or the existing `/report` acknowledgement path (T-002 implements
   endpoint arbitration while its AC says "normal report acknowledgement
   retires the Runner-local receipt"). Harmless — both are at-most-once — but
   pinning one during implementation would avoid double-building.
4. **Fence inventory vs runner in-flight set.** If the notification carries
   only the operation id, the Runner may emit receipts for works the fence
   doesn't name (server rejects terminally — safe but noisy). Carrying the
   affected-work inventory in whatever channel MF-1 specifies resolves this
   cheaply.
5. **T-002 interim retryable semantics** disappear at T-004; tests should not
   cement the retryable behavior as a permanent contract.
6. **Open questions** (stop-budget and recovery-wait defaults, AgentJob input
   envelope, recovery read-model endpoint location) are appropriately scoped
   for implementation-time resolution and do not block the plan.

<promise>FAIL</promise>
