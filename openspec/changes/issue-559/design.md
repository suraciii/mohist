## Current State

The static `mohist/agent` manifest validates task-only input without querying
live Agent state. At dispatch, `WorkflowItemTranslator` resolves the Agent and
rewrites the task to a concrete runtime Action. The Workflow task remains the
active execution owner.

Direct Agent launch already has a generic ownership model: AgentJob owns
admission, Runner claim, runtime execution, and terminal facts; AgentSession
owns physical runtime state. The missing boundary is a durable handoff from a
single Workflow task attempt to a future AgentJob invocation.

## Ownership

| Fact | Owner |
| --- | --- |
| Workflow task ordering, recovery, expectation, artifacts, and variables | WorkflowRun / TaskRun |
| Agent admission, Runner claim, execution, and terminal fact | AgentJob |
| Input, Turn, transcript, activity, and physical binding | AgentSession |

`WorkflowAgentInvocation` is immutable linkage only. It does not mirror
AgentJob status, Runner identity, runtime state, transcript, or provider data.

## Handoff Fence

One work attempt provides a stable command identity and rendered Agent input.
The handoff grain derives a canonical fingerprint and persists the first
result:

- `Prepared`: the generic Agent definition was resolved once and frozen with
  deterministic Job, Session, Input, and Turn identifiers.
- `Rejected`: a definite preflight failure was frozen. A replay cannot become
  prepared after mutable Agent configuration changes.
- `Accepted`: the matching Workflow acceptance receipt was persisted.

The same command and fingerprint replay the persisted result. A changed
fingerprint under the same command is a conflict and cannot alter the saved
invocation. A mismatched grain key is rejected before preflight.

The preflight port is a singleton grain-safe adapter that opens a short-lived
scope to resolve the generic Agent execution snapshot. It has no dependency on
Pi, OpenCode, Runner, AgentJob, or AgentSession behavior.

## Acceptance Boundary

`Prepared` and `Accepted` are non-executing states. Neither creates an
AgentJob ledger, AgentSession record, Input, Turn, or Runner work. The minted
identifiers are reserved linkage, not claims. A later activation slice may
materialize provisional participants only from a durable accepted receipt.

This fence intentionally does not switch `WorkflowItemTranslator`. Switching
it before typed transport and a Workflow-owned finalizer would leave work with
no component authorized to apply task completion, recovery, artifact, and
variable effects.

## Delivery Order

1. Deliver this Server-only command, invocation, preflight, and receipt fence.
2. Materialize provisional generic AgentJob/AgentSession participants from an
   accepted receipt and add typed Runner transport.
3. Freeze the Workflow completion contract and add the AgentJob terminal to
   Workflow finalizer with idempotent receipts.
4. Switch new `mohist/agent` dispatch only after steps 2 and 3 are complete.
