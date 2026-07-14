# Review Report

## Result: PASS

The candidate satisfies the issue #363 acceptance criteria. `WorkflowGrain` and `RunnerGrain` no longer use broad `[Reentrant]`; Runner retains only the explicitly interleavable operations and protects mutation with `_lifecycleGate`. The poll admission semaphore and write gate are gone, while fresh claims still revalidate live registration and capacity. Covered durable handlers now propagate setup and routing failures to the dispatcher while Hermes delivery remains intentionally best-effort.

The epic sweep and legacy reconcile names are removed. Link, running-transition, and command-path start-failure recovery events are persisted in the caller `DbContext` with their authoritative state changes. Production-registered recovery handlers re-drive recompute, and cancelled prerequisites remain blocked until they are done. The real Orleans concurrency and recovery specs cover the new behavior.

Verification passed: `git diff --check master...HEAD`, `npm run build`, and `npm test`. The full test run passed 875 CLI tests, 1,390 server unit tests, 2,903 server specs, 24 architecture tests, 4,596 web tests, and 1,007 runner tests. Twelve existing tests remain skipped (three architecture and nine server specs).

## Repaired Items

None. No review repairs were needed in the final candidate.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/Epic/EpicRecoverySpecs.cs:226`
  Evidence: The integration recovery spec explicitly registers the three recovery handlers and does not recreate the service provider or dispatcher. It proves real event persistence and delivery, but not production assembly scanning plus dispatcher restart in one scenario.
  SuggestedAction: Add one recovery spec that uses the production handler registration and recreates the dispatcher before delivery.
  Verification: Persist a recovery event, construct a fresh dispatcher from the production service graph, and assert the linked epic converges.
  Status: open

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherService.cs:17`
  Evidence: Per-event handler attempts remain process-local. Restarting the dispatcher resets a poison event's retry budget, so repeated restarts can postpone dead-lettering indefinitely. This predates the candidate.
  SuggestedAction: Persist per-event/per-handler delivery state and cover retry-budget exhaustion across a dispatcher restart.
  Status: pre-existing

- [ID: item-3]
  Severity: info
  Scope: server test suites
  Evidence: The completed full test run skipped 12 existing tests: 3 architecture tests and 9 server specs.
  SuggestedAction: Track the skipped tests with their owning work.
  Status: pre-existing

<promise>PASS</promise>
