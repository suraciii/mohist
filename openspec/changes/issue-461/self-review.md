# Self Review: Issue 461

## Findings

### 1. Critical: an empty terminal receipt cannot distinguish replay from a stale binding

The plan settles operation-correlated follow-up terminal records when the endpoint returns any valid receipt array, including `[]` (`design.md:62-64`, `specs/agent-session-runtime-event-delivery/spec.md:94-108`, `tasks.json:41-42`). It separately requires events rejected for a stale physical binding to remain pending against their original identity (`specs/agent-session-runtime-event-delivery/spec.md:48-52`).

The current Session grain returns the same empty array before terminal processing when the reported binding is stale (`AgentSessionGrain.cs:918-920`) and after terminal processing when the operation lease has already been consumed (`AgentSessionGrain.cs:1074-1102`). A runner-only acknowledgement policy cannot distinguish those outcomes. The current plan can therefore settle and lose a stale terminal fact, while requiring a matching receipt instead would recreate the permanent queue fence found in the previous review.

The plan must either introduce a distinguishable Server acknowledgement despite the current non-goal, or explicitly permit stale follow-up terminal records to settle and narrow the stale-binding retention contract. It is not implementable with both current requirements intact.

### 2. High: T-001 creates the competing outboxes that the design rejects

D1 rejects a new content outbox beside `FollowupFailureOutbox` because independently draining queues can deliver a follow-up outcome or later event ahead of pending input (`design.md:31-40`). T-001 nevertheless declares itself independently usable while leaving the existing follow-up terminal outbox active until dependent T-002 migrates it (`tasks.json:31`, `tasks.json:37`, `tasks.json:46`, `tasks.json:57-59`).

That intermediate deliverable has the exact ordering race used to justify the shared outbox. The implementation graph must make host switchover, follow-up producer migration, and legacy import one atomic deliverable, or stop treating T-001 as independently usable and restructure the task boundary accordingly.

### 3. High: Workflow local-persistence failure behavior lacks integration coverage

The corrected contract requires input persistence failure to prevent a Workflow prompt from starting, while activity or terminal persistence failure after runtime start must settle without replacing the runtime result (`specs/agent-session-runtime-event-delivery/spec.md:70-74`, `design.md:48-50`). T-001 covers completed enqueues, unsettled writes, transport failures, and outbox readiness, but does not require Workflow integration tests for either rejected-write boundary (`tasks.json:18-22`). T-002 adds only the corresponding follow-up input test (`tasks.json:39`, `tasks.json:47`).

This omission is material because `RuntimeTurnObserver.onEvent` is synchronous and multiple callbacks can register writes before a rejection is observed. T-001 must verify that a rejected input write invokes no Workflow runtime, and that rejected activity/close writes from multiple synchronous callbacks remain tracked, observable, and unable to replace the original successful or failed runtime result.

### 4. High: outbox health recovery has no autonomous transition contract

The design says a persistence failure marks the outbox unhealthy and schedules recovery (`design.md:88-90`), while tasks require restored health to resume work claims and follow-up commands (`tasks.json:17`, `tasks.json:48`). No spec or design decision states what triggers recovery, how it proceeds when claims and follow-ups are gated, or what durable condition permits `ready()` to become true again.

Recovery cannot depend on another enqueue because unhealthy state prevents new execution. The plan must define an autonomous fake-time-driven persistence retry plus startup/reconnect triggers, require a successful atomic snapshot covering every retained in-memory record before restoring health, and test recovery without new work or events.

## Review Summary

- The proposal still matches the issue's network-failure, restart-recovery, and non-blocking goals, and preserves the no-cross-runner boundary.
- The corrected local-versus-Server durability wording, synchronous observer crash boundary, recording filesystem strategy, and task dependency direction are clear.
- The remaining blockers concern an impossible terminal acknowledgement distinction, a non-deliverable intermediate task state, and missing behavioral contracts/tests at local persistence failure and health recovery boundaries.

## Verdict

The plan is not ready to build.

<promise>FAIL</promise>
