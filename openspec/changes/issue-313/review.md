# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/runner/src/server/workspace-removal-handler.ts
  Evidence: `RemoveWorkspace` drops the registry entry before it verifies that the requested path is under `runnerRoot`: `workspace-removal-handler.ts:46-55` calls `dropRegistryEntryForPath(...)` at line 52, then checks `isUnderRunnerRoot(...)` at line 54. This contradicts the issue spec for outside-root removal, which says an outside path must be refused and the registry must be left untouched (`openspec/changes/issue-313/specs/runner-signalr-push-handlers/spec.md:201-203`, scenario lines 215-218). The current regression test does not catch this because it only sends an unrelated outside path while the registry entry remains inside the runner root (`packages/runner/tests/workspace-registry-integration.spec.ts:287-308`); if a stale/corrupt registry entry itself points outside the root, a refused remove still deletes that entry. This was present in the merge-base implementation too, but the current change codifies the opposite invariant in its spec and comments, so the post-build candidate does not satisfy its own acceptance evidence. [disallowed:data-safety]
  SuggestedAction: Move the runner-root containment check ahead of `dropRegistryEntryForPath` for non-missing paths, and add a regression test that registers an outside-root workspace path, invokes `RemoveWorkspace` for that same path, expects `workspace_cleanup_refused`, and verifies the registry entry is still present.
  Verification: Re-run `npm run typecheck -w packages/runner` and `npm test -w packages/runner`; the new regression test should fail before the fix and pass after it.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: packages/runner/tests/runner-signalr.spec.ts
  Evidence: The liveness/connection spec requires `withAutomaticReconnect([0, 2000, 5000, 10000, 30000])` to be preserved (`openspec/changes/issue-313/specs/runner-connection-liveness/spec.md:25-32`), and the production code currently does so (`packages/runner/src/server/runner-signalr.ts:77-80`). However the SignalR mock in `runner-signalr.spec.ts:81-83` ignores the `withAutomaticReconnect` argument, and `liveness-probe.spec.ts:15-17` says the invariant is exercised indirectly even though no test captures the interval sequence. A future regression to the retry policy would not fail the suite. [disallowed:test-coverage]
  SuggestedAction: Capture the argument passed to `withAutomaticReconnect` in the existing builder mock and assert it equals `[0, 2000, 5000, 10000, 30000]` in the handshake tests.
  Verification: Re-run `npm test -w packages/runner -- tests/runner-signalr.spec.ts` and `npm test -w packages/runner`.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: packages/runner/tests/liveness-probe.spec.ts
  Evidence: The test named `does not short-circuit start when the abort signal fires before stop is even invoked` (`packages/runner/tests/liveness-probe.spec.ts:305-320`) never calls `ac.abort()`, so the title and comments do not match the exercised path. This is not blocking because the issue spec only requires the post-stop abort window, but the misleading test makes future reconnect changes harder to review.
  SuggestedAction: Either rename the test to describe the non-aborted path it actually covers, or clarify and explicitly test the intended pre-aborted behavior.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: packages/runner/src/server/runner-signalr.ts
  Evidence: The outside-root registry-drop ordering in item-1 was already present in the merge-base `runner-signalr.ts`; this change preserved it while extracting the handler. The issue becomes review-relevant because the new spec/test comments assert a stronger invariant than the implementation actually has.
  SuggestedAction: Treat the fix as part of the issue-313 integration if the new spec is authoritative; otherwise adjust the spec/comment to match the intentionally preserved behavior.
  Status: pre-existing

## Verification

- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner` passed: 71 files, 995 tests.
- `git diff --check 99efb189cb0523bdbc116adba4b804574e8f61eb..HEAD` passed.
- `rg` over `packages/runner/src` found no remaining `normalizeMaterializePayload`, `parseSetVars`, `parseOutputs`, `readNullableString`, or `readNullableNumber` definitions from the deleted dead-code path.

<promise>FAIL</promise>
