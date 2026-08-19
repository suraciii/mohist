# Design: Generic Reasoning Effort Capability

## Data ownership

The Agent definition owns the optional canonical `reasoningEffort`. The
execution snapshot owns the complete tuple and capability revision. A Runner
registration owns the ephemeral runtime catalog witness. The runtime adapter
owns native translation. No layer copies native Pi names into the generic
definition.

## Resolver ordering

1. Normalize the saved effort to the canonical enum or leave it unset.
2. Read one immutable runner catalog snapshot.
3. Require a complete entry and matching capability revision.
4. Validate model, effort, and variant independently.
5. Return a typed disposition and the compatible runner identity.
6. Only a `supported` disposition may reach claim/dispatch.

The resolver is pure and has no retry, sleep, network call, or side effect.
Readiness is a separate `unavailable` fact; it must not be confused with a
configuration mismatch.

## Alternatives rejected

- **Store native `thinkingLevel` in `variant`:** couples the generic contract
  to Pi and silently loses effort semantics for other runtimes.
- **Let every Runner interpret the saved effort independently:** produces
  different admission decisions and cannot provide one durable explanation.
- **Treat missing catalog data as incompatible:** turns a temporarily absent
  runner registration into a terminal user configuration error.

## Claim-time capability fence

The capability revision cannot be checked by the owner after work is claimed.
It is a Runner lifecycle-gate predicate. A pending candidate is admitted only
through one immutable claim expectation:

```text literal
(owner, workId, runtime, model, reasoningEffort, variant,
 capabilityRevision, runtimeGeneration, connectionGeneration)
```

The Runner compares the expectation with its current registration catalog and
poll readiness witness while holding its lifecycle gate. It calls the owner
claim only after that comparison succeeds. The owner receives the same
expectation, verifies that its pending work id has not changed, and persists
the capability revision with the dispatch snapshot before making the work
claim-visible. A catalog update, connection replacement, runtime generation
change, missing witness, or `ready=false` result leaves the candidate pending;
none is reported as a work result.

`reasoningEffort` and `variant` remain generic values in this expectation. The
Runner adapter receives them only after the claim succeeds and maps its own
runtime-native options privately.

## Owner projections

The current claim APIs cannot produce this expectation:

- `DispatchService.AddPendingDispatchesAsync` calls
  `IRunnerGrain.TryClaimAgentJobAsync(jobId, projectId)` with no capability
  evidence. AgentJob must add a read-only pending-dispatch projection and a
  conditional claim that persists the chosen revision with its ledger
  dispatch.
- `DispatchService.ClaimAndRenderWorkflowAsync` calls
  `TryClaimWorkflowAsync` before `WorkflowItemTranslator` resolves
  `mohist/agent`. Workflow must instead expose a read-only next-work
  projection with a stable pending work id. DispatchService translates that
  projection, derives the immutable capability expectation, and performs a
  conditional Workflow claim for the same work id. The existing
  `StoreActiveWorkDispatchAsync` remains the durable first-writer snapshot
  after that successful conditional claim.
- `IRunnerGrain` must accept the expectation on both claim methods and compare
  it with its current `RunnerInfo` catalog under `_lifecycleGate`. It cannot
  rely on the earlier `RunnerRegistryGrain` read, because heartbeat repair can
  replace the catalog before claim.

The Runner poll body must carry the generation-bound readiness witness defined
in `design/runner.md#runtime-readiness-witness`; catalogs are capability
witnesses, not readiness witnesses. The Server validates that witness against
the registration connection before constructing either owner expectation.

This is intentionally one cross-owner protocol. Adding catalog fields or an
adapter before these conditional claims would create a dispatch snapshot that
can be stale at the point where work becomes visible, which is not a valid
reasoning-effort implementation.
