# One Ledger, No Reconciliation

## Background

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
