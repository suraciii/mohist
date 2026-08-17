## Why

After a recovered AgentSession is stopped without an authoritative result, the Workflow correctly records an unknown settlement and waits for its durable deadline. At that deadline, however, the task and run can remain active for runner accounting, retaining a Runner slot indefinitely even though the session is idle and recovery is available; this blocks capacity and leaves the failed execution point without a deterministic liveness boundary.

## What Changes

- Make the unknown-result deadline an exactly-once settlement boundary that preserves the original unresolved/stop disposition while releasing the attempt's active-work ownership and Runner capacity reservation.
- Keep the expired attempt addressable by its original WorkflowRun, TaskRun, Work, Runner, AgentSession, AgentTurn, and runtime identity for late authoritative result arbitration, but exclude it from redelivery, fresh claims, and active-slot accounting.
- Make deadline cleanup idempotent across reminder replay, grain activation, and partial cleanup, including settlement dispatch state and other active-work reservations that would otherwise keep the work held.
- Continue to expose the outcome as blocked/unknown with its persisted reason and deadline. The deadline MUST NOT infer success or failure, replay the old turn, auto-retry, or create replacement work.
- Accept a late authoritative result only through the existing full identity fence. Apply the original result at most once; duplicate or stale receipts MUST have no side effects and MUST NOT reacquire the released work or Runner slot.
- Add deterministic fake-time and failure-injection coverage for stop to unknown settlement, deadline release, exactly-once cleanup, slot availability, and late-result arbitration.

## Capabilities

- `workflow-agent-settlement-liveness`: Durable deadline settlement for unresolved Workflow Agent executions, including release of active work and Runner capacity, preservation of blocked/unknown status and execution identity, idempotent cleanup, and identity-fenced late-result handling.

## Impact

- **Workflow server:** Agent result settlement in `WorkflowGrain` and `WorkflowRun`, TaskRun/WorkflowRun active-state and assignment semantics, settlement reminders, dispatch snapshots, stage/resource ownership, and blocked status/event projections under `packages/server/src/Mohist.Server/Workflow`.
- **Runner server boundary:** Workflow redelivery, active-work discovery, Runner slot accounting, and report acknowledgement under `packages/server/src/Mohist.Server/Runner`; released settlements must no longer appear as active Runner work while late receipts remain routable to the original identity.
- **APIs and consumers:** Workflow status, task status, Runner capacity, Issue/Inbox attention, and event projections must continue to show an actionable blocked result without treating it as a running reservation.
- **Tests and dependencies:** Server and Runner fake-time/failure-injection tests will cover the deadline race and cleanup retries. No new external dependency or Runner slot-policy redesign is required.
