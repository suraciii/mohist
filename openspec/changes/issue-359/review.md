# Review Report

## Result: FAIL

## Repaired Items

- (none)

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/runner/src/runtime/host.ts
  Evidence: The candidate changes the production interval floor for both cleanup convergence and workspace cleanup from 1000ms to 50ms at `packages/runner/src/runtime/host.ts:61-62` (`git diff master...HEAD` shows both `Math.max(1000, ...)` calls became `Math.max(50, ...)`). That means an operator setting `CLEANUP_LOOP_INTERVAL_MS=1` or `CLEANUP_CONVERGENCE_INTERVAL_MS=1` now gets a 50ms loop instead of the previous 1s safety floor. This is not required for the config-channel change, it was introduced to make `runner-host-cleanup-config.spec.ts:183` use a 50ms test interval, and it conflicts with the issue/spec non-goal that cleanup cadence and convergence behavior remain unchanged. [disallowed:reason] Repair would change runtime behavior and test strategy, so it is outside the small local repair policy.
  SuggestedAction: Restore the 1000ms production floor, and adjust the tests to use fake timers with a 1000ms interval or another test-only seam that does not alter runtime cadence safeguards.
  Verification: Re-run `npm run typecheck -w packages/runner` and `npm test -w packages/runner`; add or update a host unit test that asserts sub-1000 configured cleanup/convergence intervals are clamped to 1000ms if that safeguard is intentional.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Runner/Api/RunnerConfigApiSpecs.cs
  Evidence: `Poll_DispatchBody_NoLongerContainsCleanupPolicy` at `packages/server/tests/Mohist.Server.Tests/Specs/Runner/Api/RunnerConfigApiSpecs.cs:287-323` does not seed dispatchable work. It posts to `/poll` on an idle registered runner, then exits successfully when the response is `204 No Content` at lines 309-315. That means the test can pass without ever observing a `200 OK` `WorkDispatchResponse`, so it does not verify the acceptance criterion in `openspec/changes/issue-359/tasks.json:47` or the spec scenario in `openspec/changes/issue-359/specs/poll-policy-decoupling/spec.md:5-8` that a dispatch body omits `cleanupPolicy`. The product code currently removes the field from `WorkDispatchResponse`, but the regression guard for the wire payload is not meaningful. [disallowed:reason] Repair requires choosing or building the correct dispatch seeding path in integration tests, which is more than a small local review repair.
  SuggestedAction: Replace the idle fallback with a deterministic dispatch case: seed/assign a real dispatchable work item for the registered runner, call `POST /api/runner/{runnerId}/poll`, assert `200 OK`, and assert the JSON body has all expected dispatch fields but no `cleanupPolicy`. Keep the separate idle `204` assertions in the existing idle tests.
  Verification: Run `dotnet test Mohist.sln -p:SkipWebBuild=true` and confirm the new dispatch-body test fails if `CleanupPolicy` is reintroduced to `WorkDispatchResponse` or populated by `/poll`.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Api/RunnerRoutes.cs
  Evidence: The new `/config` endpoint satisfies the issue requirement that policy comes from server-bound `CleanupPolicyOptions` and not from runner-side config files (`RunnerRoutes.cs:139-144`, `RunnerRoutes.cs:483-488`). The endpoint locally overrides null serialization through `RunnerConfigJsonOptions` at `RunnerRoutes.cs:503-507`; this is correct for the present-null contract, but it intentionally diverges from the global `JSON.Options` surface.
  SuggestedAction: If future runner config fields need custom converters or global JSON behavior, consider deriving this endpoint's options from the shared options and only overriding `DefaultIgnoreCondition`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: verification
  Evidence: `dotnet test Mohist.sln -p:SkipWebBuild=true` passed with 3821 passed and 13 skipped tests; the skipped tests are existing suite skips, not failures introduced by this change. `npm run test:run -w packages/web` passed with 4299 passed and 1 skipped test; no web files changed in this candidate.
  SuggestedAction: None for issue 359.
  Status: out-of-scope

## Verification

- `mo issue show 359 --project-id proj_f6c141d63b6243bfbb481737b2243b87` reviewed for acceptance criteria and scope.
- Read `openspec/changes/issue-359/proposal.md`, `design.md`, `tasks.json`, all three delta specs, and `self-review.md`.
- Inspected `git diff master...HEAD`, all changed product files, all changed test files, and adjacent cleanup/recovery/artifact paths including `cleanup-loop.ts`, `cleanup-convergence.ts`, `cli.ts`, existing runner host mocks, and existing cleanup-policy/status specs.
- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner` passed: 67 files, 921 tests.
- `dotnet test Mohist.sln -p:SkipWebBuild=true` passed: 3821 passed, 13 skipped.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 269 files, 4299 passed, 1 skipped.

<promise>FAIL</promise>
