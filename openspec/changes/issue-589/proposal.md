## Why

Workflow currently turns an unconfirmed physical Agent stop or Runner loss into `TaskFailed`, even though neither fact establishes whether the Agent work succeeded or failed. Five observed runs have already recorded false terminal failures, so Workflow needs to preserve uncertainty until the original execution can be authoritatively reconciled or reaches a bounded blocked outcome.

## What Changes

- Preserve the original Workflow Agent execution identity and an explicit unknown, recoverable outcome whenever stop delivery, stop confirmation, or Runner connectivity is inconclusive; do not record `TaskFailed` or completion from those physical execution facts.
- Hand AgentSession execution observations to a Workflow-owned settlement that distinguishes physical activity from task outcome and accepts an authoritative Agent result exactly once.
- Make repeated delivery, reconnect, and recovery reconcile the recorded execution identity idempotently. Recovery requests another physical stop only when that same target is still active, and stale observations cannot overwrite an authoritative result.
- Apply a bounded settlement deadline. If no authoritative result arrives by the deadline, expose the Workflow task as `blocked` with an actionable reason and retain the documented recovery path instead of inventing a success or failure.
- Preserve the execution identity and unresolved reason across Runner disconnect and reconnect so reconciliation continues rather than creating a replacement outcome.
- **BREAKING**: Workflow task and run status consumers must handle the new visible blocked settlement state instead of receiving `failed` / `TaskFailed` for an unresolved Agent execution.

## Capabilities

- `workflow-agent-result-settlement`: The identity-preserving settlement contract for Workflow-owned Agent tasks, covering unknown physical outcomes, authoritative-result arbitration, idempotent recovery and replay, bounded transition to blocked, and actionable status projection.

## Impact

- **Server Agent/Session boundary:** AgentSession stop delivery and recovery, Workflow session bindings, and physical execution fact handoff under `packages/server/src/Mohist.Server/Sessions` and `Agent`.
- **Server Workflow boundary:** Task/run state machines, report and abandonment commands, durable events/state, recovery reminders, Runner-loss handling, and status/read projections under `packages/server/src/Mohist.Server/Workflow` and `Runner`.
- **Runner:** Workflow Agent result semantics, cancel-operation reconciliation, reconnect replay, and result acknowledgement under `packages/runner/src/actions`, `runtime`, and `server`.
- **API and clients:** Workflow task/run status payloads, event projections, CLI rendering, and Web workflow status presentation must expose and understand the blocked outcome and its reason.
- **Dependencies:** Builds on the durable identity-based physical stop operation from issue #562; it does not redesign that stop protocol or move stop retry ownership.
