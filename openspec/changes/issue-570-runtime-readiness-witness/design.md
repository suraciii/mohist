# Design: Runtime Readiness Witness

## Witness shape

The proposed wire shape is:

```json
{
  "runtime": "pi",
  "ready": true,
  "generation": 7,
  "connectionGeneration": "runner-1:conn-42"
}
```

`runtime` is canonical and case-normalized. `generation` changes whenever
the Runner replaces or rebuilds the runtime. `connectionGeneration` changes at
registration/reconnect. The Server accepts a witness only from the current
Runner identity and current connection generation; an absent or mismatched
value is unknown.

## Server algorithm

1. Admit the poll and refresh presence as today.
2. Reconcile redelivery for reported/owner-running work without applying the
   new-claim readiness gate.
3. For each pending candidate, obtain an immutable runtime projection:
   workflow work from the pending `WorkItem`; AgentJob work from its persisted
   dispatch snapshot.
4. Skip candidates whose runtime is unknown or whose witness is not ready and
   current.
5. Call the owner claim operation only after step 4. Render the workflow
   dispatch and persist its snapshot after the claim.

The candidate loop must continue to later candidates when an earlier runtime
is unavailable. It must not claim the earlier candidate as a placeholder, and
must not use a sleep or polling loop inside one request.

## Runner algorithm

The Runner reports all runtime witnesses, including `ready=false`, on every
poll. It includes its complete `inFlight` and `awaitingAck` sets on the same
request. Runtime readiness suppresses only new admission on the Server. The
Runner may keep already-held work in its existing bounded in-flight/report
state and must keep reporting until the owner returns a durable acknowledgement.

The current whole-poll `isReadyForClaim()` gate must be replaced by this
split: held-work reconciliation is allowed while a runtime is unhealthy, while
new work is admitted only from a Server response whose runtime matched a
current witness.

## Failure handling

- Missing witness: no new claim.
- `ready=false`: no new claim for that runtime; other ready runtimes may be
  served.
- Stale generation or connection: no new claim; refresh through the next
  registration/heartbeat/poll.
- Runtime cannot be resolved without owner mutation: leave work pending.
- Runtime fails after claim: keep existing in-flight and uncertain-result
  handling; never replay the original input from the readiness path.

## Focused contract scenarios

The executable implementation must add these tests:

1. `UnknownWitness_DoesNotClaimWorkflowOrAgentJob`: with no witness for the
   candidate runtime, both owner ledgers remain pending and no dispatch is
   returned.
2. `ReadyWitness_ClaimsOnlyMatchingRuntime`: a `pi` witness may claim Pi work
   but cannot claim OpenCode work in the same poll; a later ready witness can
   claim the skipped candidate without replaying the first.
3. `StaleWitness_DoesNotClaimAfterRuntimeGenerationChanges`: a witness from a
   previous runtime or connection generation is rejected even when `ready=true`.
4. `UnhealthyRuntime_StillReconcilesHeldWork`: `ready=false` does not suppress
   reporting/redelivery decisions for work already listed in `inFlight` or
   `awaitingAck`, and it never creates a deferred claim.
5. `WorkflowRuntimeProjection_IsReadOnlyBeforeClaim`: resolving `uses` for a
   pending workflow does not change assignment, status, stage lock, or task
   attempt before the witness predicate passes.

These tests are intentionally not added to the current codebase: the DTO,
projection, and admission API required to express them do not exist yet. The
scenarios are the acceptance contract for the atomic implementation slice.
