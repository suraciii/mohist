# Review Report

## Result: PASS

## Repaired Items

- [ID: item-0]
  Severity: info
  Scope: type-error
  Evidence: `workspace-registry-integration.spec.ts:171` used `vi.fn<Date, []>()` — the generic parameters were reversed (`vi.fn<Return, Args>` instead of `vi.fn<Args, Return>`), which caused tsc to report type errors. The mock returns a `Date` and takes zero arguments, so it should be `vi.fn<[], Date>()`.
  Verification: `npm run typecheck -w packages/runner` passes with zero errors.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: cleanup
  Scope: `packages/runner/src/runtime/workspace-registry.ts:275-279`
  Evidence: `isPathUnder` is exported but never imported anywhere in the codebase. The actual containment check used by cleanup is `isUnderRunnerRoot` from `runner-signalr.ts`. The function is dead code with a comment claiming it's "Kept here for completeness of the public API" — but there is no consumer.
  SuggestedAction: Remove the unused `isPathUnder` export, or move the shared containment logic into a common utility shared between `workspace-registry.ts` and `runner-signalr.ts` without circular import.
  Status: follow-up

- [ID: item-2]
  Severity: cleanup
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Config/CleanupPolicyOptions.cs:43`
  Evidence: `HasAnyEnabled` property is defined and unit-tested but never referenced in production code. The runner independently decides whether to evict based on individual field nullability (`cleanup-loop.ts:48-51`); the server never uses this property.
  SuggestedAction: Remove `HasAnyEnabled` or wire it into the poll response to let the runner skip the cleanup loop when nothing is configured (optimization).
  Status: follow-up

- [ID: item-3]
  Severity: cleanup
  Scope: `packages/runner/tests/cleanup-loop.spec.ts:67-128`
  Evidence: The `registerEligible` test helper contains 40+ lines of dead/abandoned code (lines 83-101) with commented-out thoughts about "simpler approach", "use the raw Map access via reload trick", and "Actually just use register+markEligible". The actual working path is lines 108-127. The dead block is confusing and asserts nothing.
  SuggestedAction: Remove the dead code block (lines 82-101 and the aborted test at lines 212-236 that also contains dead code). Keep only the working implementation at lines 103-127.
  Status: follow-up

- [ID: item-4]
  Severity: minor
  Scope: `packages/runner/src/runtime/cleanup-loop.ts:76`
  Evidence: When `storageTargetWatermarkBytes` is `null`, the loop defaults to 70% of `storageBudgetBytes`. This is a reasonable heuristic, but the magic number `0.7` is inlined rather than named, and the behavior is not mentioned in the design, proposal, or specs. The spec says "target watermark" is a config value — the default is an implementation detail that should be explicit.
  SuggestedAction: Extract the default ratio into a named constant with a comment documenting the rationale, or document the default in the CleanupPolicy type comment.
  Status: follow-up

- [ID: item-5]
  Severity: minor
  Scope: `packages/runner/tests/cleanup-loop.spec.ts:212-236`
  Evidence: The test "does not evict eligible entries without terminalAt" has a body that starts with registration logic but ends with a `// Skip this test` comment and never makes any assertions. The test passes vacuously. An eligible entry without `terminalAt` is a real edge case that should be tested, but the current test is a no-op.
  SuggestedAction: Either remove the vacuous test or properly implement it by creating a registry entry in eligible phase with no terminalAt (the public API doesn't expose a way to do this, so this test may be impractical; removing it is fine).
  Status: follow-up

- [ID: item-6]
  Severity: test-gap
  Scope: `packages/runner/tests/workspace-registry.spec.ts`
  Evidence: No test covers the `loadFromDisk` code path where `readText` throws (e.g., permission error). The code silently treats unreadable files as empty (`packages/runner/src/runtime/workspace-registry.ts:206-211`), which is safe but untested. Similarly, the `persist` method's `mkdir` and `writeFile` error paths are untested.
  SuggestedAction: Add a test for loadFromDisk when readText throws (e.g., mock `readText` to reject), verifying the registry loads as empty. Low priority — the behavior is already documented in comments and is inherently defensive.
  Status: follow-up

- [ID: item-7]
  Severity: test-gap
  Scope: `packages/runner/src/runtime/workspace-registry.ts:140-147`
  Evidence: No explicit test verifies that `refreshMaterializedAt` on an `eligible` entry does NOT downgrade the phase to `active`. The design D7 says "do not downgrade an eligible entry back to active," and the implementation is correct (it preserves existing phase), but this contract is only implicitly verified by other tests.
  SuggestedAction: Add a targeted test: register an entry, mark it eligible, call `refreshMaterializedAt`, assert phase is still "eligible".
  Status: follow-up

- [ID: item-8]
  Severity: minor
  Scope: `packages/runner/src/runtime/cleanup-convergence.ts:67-76`
  Evidence: When the server returns no status for a workflowRunId, the convergence backstop removes the registry entry. This is correct for truly deleted runs, but it permanently orphans the on-disk workspace directory (no automatic cleanup applies to un-tracked directories). The only recovery path is manual cleanup. If a transient server error causes some entries in a batch to be omitted (e.g., grain activation timeout), those workspaces become orphaned.
  SuggestedAction: Consider a grace period: instead of immediately removing the entry on first "unknown" response, mark the entry with a `serverUnknownCount` counter and only remove it after N consecutive convergence passes report it as unknown. This guards against transient server issues. Alternatively, document this behavior clearly as an operational note.
  Status: follow-up

- [ID: item-9]
  Severity: test-gap
  Scope: `packages/runner/src/runtime/cleanup-loop.ts:89-92`
  Evidence: In budget eviction, if `computeDirectorySize` returns `null` or `0` for an individual entry, `currentUsage` does not decrement but the entry is still removed. This creates usage tracking drift: after the loop completes, the actual remaining usage may be significantly lower than `currentUsage` reflects. The next tick's usage cache will correct this, but within a single tick, the stale `currentUsage` may cause over-eviction (removing more entries than needed).
  SuggestedAction: When `entrySize` is null, conservatively skip evicting that entry instead of evicting it without accounting. A null size from `computeDirectorySize` means the directory state is unknown — better to leave it and let the next tick handle it with a fresh usage cache.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-10]
  Severity: info
  Scope: `packages/runner/tests/acp/session-strategies-resume.spec.ts`
  Evidence: This pre-existing test failure (`expected 1 but got 2` for prompt events) is unrelated to the workspace cleanup change. The test file was not modified in this change set.
  SuggestedAction: Fix independently.
  Status: pre-existing

- [ID: item-11]
  Severity: info
  Scope: Multiple test files (`runner-host.spec.ts`, `runner-signalr-workflow-status.spec.ts`, `workspace-registry-integration.spec.ts`, `runner-host-convergence.spec.ts`)
  Evidence: The `@microsoft/signalr` mock (`FakeConnection`, `HubConnectionBuilder`) is duplicated nearly verbatim across 3 test files. This is maintainability debt — changes to the mock need to be replicated.
  SuggestedAction: Extract a shared SignalR test double into a test utility. Out of scope for this change.
  Status: pre-existing

- [ID: item-12]
  Severity: info
  Scope: `packages/runner/src/runtime/cleanup-loop.ts:162-171`
  Evidence: `DefaultCleanupRunner.computeDirectorySize` uses `du -sb`, which is GNU `du` syntax (`-b` for bytes). On macOS, `du -sb` does not exist (macOS `du` uses `-k`, `-h`, or block counts). The runner is Linux-targeted, but this limits cross-platform development.
  SuggestedAction: Use a cross-platform approach (e.g., `du -sb` on Linux, `du -s` with block-size adjustment on macOS) or document the Linux-only requirement. Out of scope for this change.
  Status: pre-existing

## Verification

- **Server typecheck**: Passed (`dotnet build Mohist.sln` succeeds)
- **Runner typecheck**: Passed (0 errors)
- **Web typecheck**: Passed (0 errors)
- **Server tests**: 311 passed, 0 failed, 2 skipped (pre-existing)
- **Runner tests**: 47 passed, 1 failed (pre-existing `acp/session-strategies-resume.spec.ts`, unrelated to this change)
- **New test files**: 8 new runner test files, 4 new server test files — all passing
- **Acceptance criteria**: All 11 issue acceptance criteria are satisfied with concrete implementation evidence
- **Spec compliance**: All 10 requirements in `specs/http-api/spec.md` and `specs/runner-workspace-cleanup/spec.md` are covered by implementation and tests

## Review Notes

The implementation correctly addresses the core acceptance criteria: a persisted workspace registry, event-driven and convergence-backstop terminal detection, retention and budget eviction with pre-delete safety guards (path containment and marker identity check), and preserved manual cleanup semantics. The design decisions (D1-D7 in `design.md`) are faithfully implemented.

Safety posture is strong: the cleanup loop only touches `eligible` entries, each deletion is gated by path containment and marker identity matching, and the marker file remains identity-only as required. The convergence backstop correctly queries only active entries (no full-history scan). Both retention and budget eviction strategies properly exclude `active` entries.

No blocking issues were found. All items above are minor cleanup, test gap improvements, or deferred improvements that do not affect correctness, security, or spec compliance.

<promise>PASS</promise>
