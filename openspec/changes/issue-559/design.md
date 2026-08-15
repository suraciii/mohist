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

## Activation and Settlement Boundary

The fence is a provisional agreement, not an execution request. A future
implementation has two post-acceptance durable boundaries: activation and
settlement.

```text
Prepared or Rejected
        |
        | matching Workflow acceptance receipt
        v
Accepted -- no participants and no Runner work
        |
        | persisted activation cursor, using only frozen facts
        v
AgentJob + AgentSession + Input + Turn
        |
        | typed AgentJob terminal receipt
        v
Workflow finalizer receipts
        |
        v
Task outcome, artifacts, variables, recovery, and advancement
```

`Accepted` is the only state that may authorize activation. Activation must
reuse the reserved identifiers and the frozen Agent identity, execution
definition, rendered `expect`, timeout, session name, and workspace identity.
It must not call a mutable Agent launch path that re-resolves configuration.
Each participant write advances one durable cursor, so a restart or lost
acknowledgement retries the same participant and never creates replacement
work. While this support has no Workflow caller, it remains dark: an accepted
receipt alone cannot create a Job, Session, Input, Turn, or Runner work.

Submitting the AgentJob is not TaskRun completion. The Runner must report a
typed terminal record carrying the invocation, WorkflowRun, TaskRun, work,
Job, Session, Input, and Turn identities together with the Agent terminal
facts and completion evaluation. The terminal record has one stable delivery
identity per Job and remains durable until the Workflow finalizer acknowledges
its matching receipt. It must not be encoded in the Workflow task-report
payload, because AgentJob and TaskRun have different execution owners.

The Workflow finalizer validates the frozen invocation against the active task
attempt, then writes effect receipts before applying task outcome, `expect`,
artifacts, variables, recovery, or advancement. Duplicate, stale, and
post-restart terminal delivery acknowledges an existing receipt without
reapplying an effect. A terminal AgentJob can therefore be durable while its
Workflow remains awaiting finalization; it is never inferred from a Session
transcript or activity state.

The runtime-visible cutover is one boundary: `WorkflowItemTranslator` may call
handoff prepare, accept, and activation only after typed terminal delivery and
the Workflow finalizer are registered. Before that point, the existing inline
`mohist/agent` translation remains authoritative. Direct Agent launches and
inline `mohist/opencode` and `mohist/pi` tasks keep their current paths.

Recovery is driven by the durable activation or terminal-delivery obligation,
not an independent fixed-interval polling loop. Tests inject the wake-up and
time boundary, and prove a participant acknowledgement loss, terminal delivery
loss, duplicate delivery, and a restart without creating another execution.

## Delivery Order

1. Deliver this Server-only command, invocation, preflight, and receipt fence.
2. Add dark activation support with a persisted cursor and frozen participant
   plan. It has no production Workflow caller.
3. Add typed AgentJob terminal delivery and the Workflow-owned finalizer with
   per-effect receipts. Verify the full replay and duplicate-delivery path.
4. Switch new `mohist/agent` dispatch only after steps 2 and 3 are complete in
   the same deployed contract.
