## Context

The current Runner grain stores `_lastPresenceAt` only in memory. Its `presence` reminder is not an expiry supervisor, and the volatile registry returns every indexed Runner. The current master already contains the generation-fenced crash boundary from #766: `RunnerState.ClosingProcessGeneration` is persisted while the prior process generation is being failed out, and Workflow/AgentJob owners expose generation-fenced failure methods returning `WorkReportVerdict`.

This change adds only durable lease supervision and makes that existing generation closeout obligation retryable. It does not restore the removed Runner-process result journal or introduce a second owner ledger.

## Decisions

### Presence lease

Add `[Id(6)] DateTimeOffset? PresenceLeaseExpiresAt` to `RunnerState`. It is an absolute UTC expiry computed from the injected `TimeProvider` and the unchanged two-minute timeout. Register, heartbeat repair, heartbeat, and successful poll persist `now + 2 minutes` before online registry publication. The ten-second grain timer remains a low-latency path; the durable `presence` reminder is the reactivation guarantee.

Activation treats a future lease as online using the persisted profile, arms the reminder for the remaining lease, and does not mint a new lease. An elapsed lease marks the grain offline and executes the same guarded timeout convergence without renewal. Missing legacy expiry remains offline until real Runner traffic creates a lease. Explicit unregister clears the lease and persists offline state before removing registry membership.

`IRunnerGrain.IsPresenceLeaseActiveAsync` reads the activated persisted state and current injected time without renewing it. `RunnerRegistryGrain.ListEligibleRunnersAsync` asks that authority for every volatile index entry and omits any false or failed read. `ListAllAsync` remains an unfiltered diagnostic index.

### Existing generation closeout

`ClosingProcessGeneration` is the single durable obligation for one lost or replaced Runner process generation. It is set before the first closeout scan and remains set across deactivation. The existing owner APIs remain authoritative:

- Workflow `FailActiveWorkAsync(runnerId, workId, processGeneration, message)` validates the exact active work and generation and returns `Accepted` or `Refused`.
- AgentJob `FailRunnerLostAsync(runnerId, workId, processGeneration)` validates the persisted Runner/work/generation tuple and returns `Accepted` or `Refused`.

A closeout pass discovers both Workflow and AgentJob owner sets, attempts every discovered matching owner independently, and logs owner identity with Runner identity on exceptions. `Accepted` and `Refused` are definitive completion for that owner. `Outstanding`, an owner query failure, a load failure, or a delivery exception retains `ClosingProcessGeneration`; other owners are still attempted. The obligation clears only after both scans complete and no attempted matching owner remains outstanding or threw. Replays are safe because owner methods are generation-fenced and idempotent.

The `presence` reminder remains registered while either a valid lease or `ClosingProcessGeneration` exists. Activation and reminder ticks retry the same generation. A replacement registration cannot publish the new generation or reopen admission until the old generation closeout pass is complete; if it is not complete, the new registration remains rejected and the durable obligation is retried later.

### State evolution

Only the optional lease field is added to the existing Runner state. Existing fields `CurrentProcessGeneration`, `PendingProcessGeneration`, and `ClosingProcessGeneration` remain the generation closeout record. Legacy state without field ID `6` defaults offline. No per-owner pending collection, fixed owner deadline, new owner interruption/Unknown method, generic outbox, public acknowledgment API, or Runner-process journal is introduced.

## Authority matrix

| Fact | Authority |
| --- | --- |
| Current presence lease and online/offline projection | Runner grain persisted `RunnerState` |
| Registry membership | Volatile `RunnerRegistryGrain` index |
| Runner eligibility | Runner grain lease read, fail closed |
| Workflow active work and generation-fenced failure | Workflow owner |
| AgentJob active work and generation-fenced failure | AgentJob owner |
| Lost-generation delivery obligation | Runner `ClosingProcessGeneration` |
| Current time | Injected `TimeProvider` |

## Tests

Use compiled apphost focused tests with fake `TimeProvider`, explicit reminder calls, and explicit grain deactivation/reactivation. Cover lease persistence and renewal, activation before and after expiry, legacy missing expiry, stale registry eligibility, unregister ordering, closeout first failure then reminder retry, closeout retry after activation, checks closeout, owner failure isolation, generation fence, and idempotent owner replay. Do not use wall-clock sleeps, polling assertions, or external dependencies.
