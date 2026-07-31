# Review: Issue 531

## Findings

### H1. Admitted AgentJobs never reach the owner-controlled availability timeout

`AgentJobGrain.TryAdmitOnRunnerAsync` writes `ReadySince` only into the ledger record (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1000-1016`). It does not set `State.ReadySince` before serializing `StateJson`. On reload, `HydrateAsync` incorrectly copies the ledger value to `SubmittedAt` instead of `ReadySince` (`AgentJobGrain.cs:1551-1557`), so `ReadinessTimeoutExceeded` always returns false (`AgentJobGrain.cs:1157-1167`). The standard pending reminder path is also bypassed for routed launches: it repeatedly calls `AdvancePreparedLaunchAsync` before reaching the pending deadline branch (`AgentJobGrain.cs:1453-1456`), and ordinary `SubmitAsync` does not arm a reminder at all (`AgentJobGrain.cs:410-415`). `CheckTimeoutsAsync` has no production caller. An admitted job that loses its poll-time claim race can therefore remain pending indefinitely instead of failing with `runner-unavailable`. Persist and hydrate the ready time consistently, ensure every pending admission has a deadline driver, and add fake-time coverage for direct, manual, routed, and reactivated jobs.

### H2. Poll-claimed AgentJobs do not start their execution timeout

`ClaimNextAsync` atomically changes the row to Running and hydrates it, but returns without calling `ArmJobTimeout` (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:241-250`). The timer is only armed on a later activation (`AgentJobGrain.cs:124-127`), while no production code invokes `CheckTimeoutsAsync`. A claimed job whose runner never reports stays Running until an unrelated activation, which regresses the existing execution-timeout behavior. Arm the timeout immediately after a successful claim and add a poll-driven fake-time spec that does not call the grain's test-only timeout method explicitly.

### H3. The legacy ledger migration accepts malformed nonterminal work

`ValidationSql` is a plain `SELECT 1` (`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260729000000_AgentJobOwnerLedger.cs:204-222`). `MigrationBuilder.Sql` executes it and discards its rows, so a malformed record does not abort the migration as the surrounding comments and acceptance criteria claim. The following backfill writes incomplete Running/Pending projections, making owner-led recovery impossible for that work. Replace the query with SQL that raises on the first invalid row (and include missing runner/work/input/running-time cases), preserving the transaction rollback; add migration specs proving no row is changed when any nonterminal legacy record is malformed.

### H4. The migration timestamps pending work at code-authoring time, not migration time

The owner-ledger migration hard-codes `2026-07-29T00:00:00Z` (`20260729000000_AgentJobOwnerLedger.cs:33`) and writes it to every migrated pending/unknown row (`:290-329`). Applying the migration after the configured availability bound has elapsed makes all legacy pending work already overdue once H1 is fixed, rather than giving it the required deadline from the migration. Obtain one timestamp at migration execution and use that value for every valid pending row; cover a delayed migration in a spec.

### H5. The Runner persistent-state key change prevents post-upgrade dispatch recovery

`RunnerGrain` changes its Orleans persistent-state key from `runner-works` to `runner` (`packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:64`; the previous key was `runner-works`). There is no migration or fallback reader for the old record. After an upgrade/deactivation, `OnActivateAsync` finds no `LastKnownInfo` (`RunnerGrain.cs:76-89`); the next poll consequently returns no work at `DispatchService.PollCoreAsync` (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:66-69`) even when the owner ledger holds assigned/running work. Preserve or migrate the registration/presence state and add an upgrade recovery spec that deactivates a previously registered runner before its next poll.

### M1. Assigned AgentJob polling loads an unbounded pending backlog

`DispatchService` sets a candidate limit but applies it only after awaiting `ListAssignedPendingForRunnerAsync` (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:157-176`). The store implementation has no limit and materializes every assigned pending row before the in-memory `Take` (`packages/server/src/Mohist.Server/Infrastructure/Data/AgentJobs/AgentJobStore.cs:310-321`). A runner with a large pending backlog deserializes the entire ledger set on every poll to emit at most `max(availableSlots, 20)` candidates. Add a limit parameter to the projection query, apply it in SQL, and add a query-cost regression spec with many assigned pending jobs.

## Verification

- Compared `origin/master...HEAD` with the Issue 531 plan artifacts and acceptance criteria.
- `git diff --check origin/master...HEAD` completed without whitespace errors.
- Did not rerun the full server suite in this review-only pass.

<promise>FAIL</promise>
