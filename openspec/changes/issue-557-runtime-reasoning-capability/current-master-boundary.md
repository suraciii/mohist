# Current-Master Boundary: Claim-Time Capability Fence

Audit base: `origin/master=5a2cc1d22f889569e14543c39e417a2993003d8d`.

This note records the implementation boundary found on that exact base. It
does not claim that issue #557 is delivered.

## What is already durable

- `AgentExecutionDefinition` stores `ReasoningEffort` independently from
  `Variant` (`packages/server/src/Mohist.Server/Infrastructure/AgentExecutionSnapshot.cs:22-34`).
- `AgentJobInput` stores the saved effort and `AgentJobGrain.Dispatch` emits
  it as `with.reasoningEffort` (`packages/server/src/Mohist.Server/Agent/Grains/IAgentJobGrain.cs:616`,
  `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Dispatch.cs:31-45`).
- The Runner's Pi adapter consumes the canonical effort, while the OpenCode
  path rejects it as unsupported. This preserves the generic field but does
  not prove that a selected Runner can execute it before claim.

## Negative boundary on current master

### 1. The catalog is not an effort capability witness

`RuntimeCatalogEntry` contains only `Models` and `Variants`
(`packages/server/src/Mohist.Server/Runner/Grains/IRunnerGrain.cs:140-142`).
The Runner registration maps Pi thinking levels into the `variants` map
(`packages/runner/src/runtime/host.ts:863-880`). There is no independent
`reasoningEfforts` map, `supportsReasoningEffort`, `complete` bit, or
capability revision. A catalog-only change would therefore be unable to
distinguish an explicit incompatibility from an incomplete or legacy witness.

### 2. AgentJob admission does not consume a capability snapshot

`AgentJobGrain.TryAdmitAsync` enumerates `RunnerInfo` values from
`ListEligibleRunnersAsync` and tries pinned/home/generic runners
(`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1047-1097`).
`TryAdmitOnRunnerAsync` writes the AgentJob ledger and serialized dispatch
before the Runner poll claim (`:1241-1299`); it does not evaluate the frozen
`(runtime, model, reasoningEffort, variant)` tuple against a Runner catalog or
store a capability revision. The registry's `ListEligibleRunnersAsync`
currently returns the registered values without a capability predicate
(`packages/server/src/Mohist.Server/Runner/Grains/RunnerRegistryGrain.cs:132-140`).

### 3. Runner claim has no expected capability fence

`DispatchService.AddPendingDispatchesAsync` checks only runtime readiness and
then calls `IRunnerGrain.TryClaimAgentJobAsync(agentJobId, projectId)`
(`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:224-237`).
The claim API has no work id, execution tuple, catalog revision, runtime
generation, or connection generation. `RunnerGrain.TryClaimAgentJobAsync`
checks drain, online status, project, and capacity under `_lifecycleGate`,
then delegates to `AgentJobGrain.ClaimNextAsync` without comparing capability
facts (`packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:477-498`).
Consequently a catalog/connection update can race the earlier candidate read.

### 4. Workflow has the same ordering defect

`DispatchService.ClaimAndRenderWorkflowAsync` calls
`TryClaimWorkflowAsync` first and only translates the claimed `WorkItem`
afterward (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:322-363`).
The Runner claim itself commits the workflow's `ClaimNextAsync` before that
translation (`packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:436-469`,
`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:482-510`).
Because the current candidate query exposes only a run id, a catalog or
readiness check added only to AgentJob would leave Workflow agent work with a
different and unsafe claim order.

### 5. Unavailable and incompatible outcomes are not interchangeable

`AgentReadinessService` derives executability from structural config and the
latest job history, not from the current Runner catalog
(`packages/server/src/Mohist.Server/Agent/Services/AgentReadinessService.cs:58-83`).
After a Runner returns a non-success result, `AgentJobGrain.ReportResultAsync`
enters the terminal path (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:344-360`).
Therefore a Runner-side rejection cannot be used as the implementation of
"temporarily unavailable: keep pending"; that outcome must be decided before
claim and returned as a non-terminal owner transition.

## Required atomic protocol

The next implementation must land these pieces together:

1. Extend the runtime catalog with append-only `reasoningEfforts`,
   `supportsReasoningEffort`, `complete`, and a capability revision. Keep
   native runtime mapping private to the Runner adapter; never encode effort
   as generic `variant`.
2. Add a read-only pending-dispatch projection for AgentJob and a read-only
   next-work projection for Workflow. Both projections must expose the same
   stable work identity and frozen execution tuple without claiming or
   mutating the owner.
3. Define one claim expectation containing owner/work identity, the frozen
   tuple, capability revision, runtime generation, and connection generation.
   `IRunnerGrain.TryClaimAgentJobAsync` and `TryClaimWorkflowAsync` must accept
   it and compare it with the current registration and readiness under the
   Runner lifecycle gate.
4. Both owner claims must be conditional on the expected work identity and
   tuple. A failed readiness/catalog comparison leaves the owner pending. A
   complete explicit incompatibility records a deterministic preflight
   failure with the frozen tuple; it must not fall back to another model,
   effort, variant, or runtime.
5. Add focused race tests for both owner kinds: catalog replacement between
   projection and claim, connection/runtime generation replacement, missing or
   incomplete catalog, explicit incompatibility, and temporary runtime
   unavailability. Existing redelivery behavior remains separate.

Until this protocol exists, a pure evaluator, extra catalog fields, or a
Runner adapter is useful test substrate but is not a safe claim-time
implementation of #557.
