# Workflow Claim Boundary

Stage 1 closes the explicit-reasoning-effort fence for `AgentJob` only.
Workflow claim remains on the existing API until its owner transaction can
persist all of the following together:

1. the stable pending `WorkId` and frozen `(runtime, model,
   reasoningEffort, variant)` tuple;
2. the `TaskStarted`/running owner state; and
3. the first frozen `WorkDispatch` binding snapshot.

The current Workflow path claims the `WorkItem` before translation and stores
the active dispatch through a separate snapshot store. It therefore MUST NOT
accept or ignore `CapabilityClaimExpectation`, and this Stage 1 change does
not claim that Workflow capability admission is implemented. A future slice
must add a read-only next-work projection, pass one expectation through the
Runner lifecycle gate, and perform the owner-side running transition plus
dispatch snapshot write in one transaction. A failed predicate must leave the
Workflow item pending and must not append `TaskStarted` or a dispatch
snapshot.
