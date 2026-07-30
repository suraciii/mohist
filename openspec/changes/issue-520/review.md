# Review: Issue 520

## Findings

### H1. Concurrency reconciliation removes permits for live executions

`packages/server/src/Mohist.Server/Agent/Grains/AgentConcurrencyGrain.cs:144-151` calls `ReconcileAsync` with a newly created, always-empty token set. `ReconcileAsync` then removes every active token before granting queued waiters (`:91-95`). This runs both on grain activation (`:28-34`) and every 30 seconds through the reminder (`:106-109`).

As a result, a still-running AgentJob or an active follow-up stops counting toward `MaxConcurrentRuns` at the next reconciliation tick (or after the gate reactivates), and queued work can be granted beyond the configured limit. This directly violates the active-execution bound and the required orphan-only reconciliation. Reconciliation must derive the live Job and Session execution tokens from their authoritative state, retain those tokens, and prune only confirmed orphans. Add coverage for a running launch and an active follow-up surviving reminder and activation reconciliation, as well as actual orphan reclamation.

### H2. Jobs waiting for runner capacity consume active-execution permits

`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:968-1002` acquires a permit before looking for a Runner, but when no runner exists or every runner is full it only schedules another dispatch attempt. The permit is released only after a failed assignment that reaches `:1094-1100`; the no-runner and no-slot paths never release it.

A pending job therefore counts as an active run even though it has not started. With `MaxConcurrentRuns = 1`, submitting one job while all runners are offline makes later jobs wait for `concurrency-limit`, rather than the required `no-online-runner`; the same applies to a full runner. It also conflicts with the planned assignment-failure release behavior. Release the permit whenever runner assignment cannot proceed, or model reservations separately so the concurrency count and Availability conclusion continue to represent active executions only. Add launch-level coverage for no-runner and full-capacity waits under a finite limit.

### M1. Needs-setup responses advertise a literal, non-routable setup path

`packages/server/src/Mohist.Server/Agent/Services/AgentReadinessService.cs:42` defines the setup path as the literal string `/agents/{agentId}/settings`; it is never expanded with the actual Agent id. The Web router only provides the Agent detail route at `agents/:agentId` (`packages/web/src/app/App.tsx:74`), and the detail and CLI renderers only print this value (`packages/web/src/pages/agent-detail/ui/AgentDetailPage.tsx:166-170`, `packages/cli/Mohist.Cli/TableRenderer.Entities.cs:142-147`).

Consequently, the actionable setup entry required for `Needs setup` sends users to no usable destination. Return a concrete path to an existing edit surface, or add and consume a real settings route; test the server response with a real Agent id rather than a mocked readiness object.

### M2. The required Web test gate fails

`packages/web/src/pages/agent-detail/ui/AgentDetailPage.test.tsx` is 529 lines. `npm test` fails at `npm run check:test-boundaries -w packages/web` because the repository limits new Web test files to 500 lines, so the Web Vitest suite does not run.

Split the test by product subject or move shared test support so it satisfies the file-size rule, then rerun the complete required suite.

## Verification

- `npm run typecheck -w packages/web` passed.
- `npm run check:fsd -w packages/web` passed.
- `npm test` failed at the Web test-boundary gate; .NET, CLI, Server, and Runner tests completed successfully before that failure.

<promise>FAIL</promise>
