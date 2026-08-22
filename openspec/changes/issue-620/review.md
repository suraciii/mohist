# Review: issue-620

## Verdict

**FAIL** — one must-fix coverage gap leaves an issue acceptance criterion unverified.

## Must-fix findings

### MF-1 — Retryable terminal presentation has no deterministic end-to-end verification

**Violates issue acceptance criterion 7** ("Retry presentation ... has deterministic verification").

The new production path is in `packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs:53-81`: for a retryable failed delivery it resolves the durable Session/Turn, creates the signed action, and enqueues an explicit failure outbox entry with Retry blocks. No test exercises this path.

`packages/server/tests/Mohist.Server.SpecTests/Specs/Slack/SlackTerminalDeliveryHandlerSpecs.cs` only covers the pre-existing reaction-only behavior, using `failureCategory = "runtime-failed"` (non-retryable), completed/cancelled/unknown cases, and Manager deliveries. Those cases therefore all bypass the new branch. `SlackRetryInteractionSpecs` tests `SlackRetryActionService.CreateRetryActionAsync` and the interaction route directly, but never feeds a terminal delivery event through `SlackTerminalDeliveryHandler` or inspects the resulting `ExplicitFailure` outbox payload.

As a result, the change does not verify that a real retryable terminal event actually renders a Retry button with the correct action value, durable Session/Turn identity, Slack message/thread target, and five-minute expiry, nor that the new path falls back to reaction-only when signing material or durable facts are unavailable. Add deterministic handler-level coverage for the positive path and the relevant negative/fallback cases, including assertions on the actual outbox blocks/action payload. The implementation task can then catch regressions in event-field propagation, outbox promotion, and signing-material handling rather than only testing the action service in isolation.

## Dimension checks

- **Acceptance criteria — FAIL:** the retry operation, authorization revalidation, root/thread attempt paths, restart worker, allowlist, and cleanup are implemented, but the presentation portion of criterion 7 is not deterministically verified.
- **Correctness — checked, no additional must-fix issue found:** the reviewed paths use the single `AgentSessionRetryPolicy`, preserve the failed Turn, persist the operation before dispatch, use preallocated identities, target follow-up dispatch explicitly, and route Retry through the existing lease/outbox interaction surface.
- **Consistency — checked, no additional must-fix issue found:** Stop continues to use the same route and signing behavior; the adapter changes are test-only; the runner category mapping is shared between AgentJob and follow-up handling.
- **Tests — FAIL for MF-1:** UnitTests (3793) and SpecTests (3056) pass, runner tests (1690) pass, and TypeScript Slack adapter tests (91) pass. Those suites do not cover the missing terminal-presentation path described above. Go adapter tests could not be run because this workspace has no `go` executable; that is recorded as an observation, not a verdict driver.

## Observations

- `AgentRetryOperationStore.FindAsync` scopes lookups by `ProjectId` (`packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentRetryOperationStore.cs:189-199`), while the migration creates global unique indexes for `IdempotencyKey` and `(SessionId, TurnId)` (`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260912000000_AddAgentRetryOperations.cs:44-54`). If the same idempotency key is legitimately used in two projects, the second insert can hit the global unique constraint and then fail to find the winner because the read is project-scoped. The issue does not state a cross-project idempotency-key policy, so this is an implementation-consistency concern rather than a must-fix finding for this review.
- Go adapter verification remains unavailable in this environment because `go` is not installed.

<promise>FAIL</promise>
