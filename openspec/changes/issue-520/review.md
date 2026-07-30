# Review: Issue 520

## Findings

### H1. Reconciliation can still remove a permit before its owner records it

`AgentConcurrencyGrain.AcquireAsync` persists an active permit before returning (`packages/server/src/Mohist.Server/Agent/Grains/AgentConcurrencyGrain.cs:66-70`), but the authoritative owner is only recorded afterwards: `AgentJobGrain` sets and saves `ConcurrencyPermitHeld` after the RPC returns (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1020-1035`), and `AgentSessionGrain` persists the follow-up lease after the same RPC returns (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:444-455`).

If activation/reconciliation runs in that cross-grain window, `ReconcileFromAuthoritativeStateAsync` queries the not-yet-updated Job/Session state and deletes the freshly granted permit (`AgentConcurrencyGrain.cs:144-175`). The caller then records its lease/held flag and proceeds to Runner dispatch with no permit in the gate, so another execution can be granted beyond `MaxConcurrentRuns`. This is the same active-execution-bound failure under a grant/reconciliation race, rather than the previous steady-state reconciliation failure. Make the grant-to-owner handoff recoverable as one protocol (for example, retain a durable pending grant until the owner acknowledges it, or persist an owner claim before the gate can reconcile it), and add deterministic Job and follow-up tests that force reconciliation between `AcquireAsync` returning and the owner persistence.

### M1. Waiting-work reasons become false after capacity recovers but before dispatch retries

`AgentAvailabilityService.BuildWaitingWork` has no per-Job waiting reason. For every Pending Job that is not currently in the concurrency waiter list, it copies the *current aggregate* availability reason and falls back to `no-online-runner` when availability is now ready (`packages/server/src/Mohist.Server/Agent/Services/AgentAvailabilityService.cs:122-134`). `AgentJobGrain` deliberately keeps a job Pending until its next backoff timer after an offline/full-runner attempt (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:989-1005`, `:1180-1192`).

Consequently, when a Runner or slot returns during that backoff interval, the status endpoint reports `CanStartNow`, but still lists the Pending Job as waiting for `no-online-runner`. Web and CLI render that incorrect server-provided reason. This violates the requirement that each waiting item state what it is actually waiting for. Persist/update a Job-level waiting reason (or immediately re-dispatch and remove the item when availability changes) and cover the transition from offline/full to available before the scheduled retry.

## Verification

- Inspected the Issue 520 acceptance specs, implementation, and the previously recorded complete suite results.
- Did not rerun the complete suite during this review-only pass.

<promise>FAIL</promise>
