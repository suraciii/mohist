# One Ledger, No Reconciliation

Status: accepted

## Problem

AgentJob work was previously delivered over a push channel. AgentJobGrain
pushed DispatchSnapshot across grains into staging in the Runner aggregate,
which persisted a second work record. Periodic reconciliation then compared
the staged record with its owner. Reconciliation was not a design feature; it
was the carrying cost of redundant state, together with cross-grain callback
cycles, races between assignment and poll, and ledger hydration on activation.

## Decision

AgentJob, like WorkflowRun, is its own dispatch ledger. Dispatch fields
(`Status`, `AssignedRunnerId`, `ReadySince`, and `DispatchSnapshot`) are
persisted in a queryable projection. DispatchService computes desired work
identically for both owner types, and the owner completes claim atomically.
The Runner aggregate returns to presence, slots, and closeout without work
records. The cross-grain cycle of assignment callbacks and runnable reverse
lookups disappears, and capacity decisions converge at claim. The old push
channel's Runner-side staging, reconciliation loop, and dispatch retry state
machine (`DispatchAttempts`, retry bound, and acceptance fence) are deleted
together. The owner handles the case of an AgentJob with no available Runner
through its own ReadySince timeout.

## Alternatives considered

**Keep the push channel with Runner-side staging and reconciliation.**
Rejected: the staged copy is redundant state, and reconciliation is the
carrying cost of keeping it, with races between assignment and poll built in.

## Consequences

- One work record exists per AgentJob, and claim is atomic at the owner.
- Capacity decisions converge at claim instead of being arbitrated across a
  staged copy and its owner.
- The Runner aggregate carries presence, slots, and closeout only; no work
  records, staging, or reconciliation state survive.
