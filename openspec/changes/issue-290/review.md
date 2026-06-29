# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs` reminder lifecycle
  Evidence: `RunnerGrain` is `[Reentrant]` (`RunnerGrain.cs:20`) and the new `EnsureWorkTimeoutReminderAsync` returns early when `GetReminder("work-timeout")` finds an existing reminder (`RunnerGrain.cs:868-878`). That creates a lifecycle race with the existing unregister path. A drain path can observe no active work and enter `MaybeUnregisterWorkTimeoutReminderAsync` (`RunnerGrain.cs:404-405`, `RunnerGrain.cs:535-536`, `RunnerGrain.cs:175-176`), get the existing reminder (`RunnerGrain.cs:890`), then yield. While it is yielded, a new assignment can add outstanding work and call `EnsureWorkTimeoutReminderAsync` (`RunnerGrain.cs:309-316` or `RunnerGrain.cs:746-753`). Because the old reminder still exists at that instant, the new guard returns without registering. When the drain path resumes, it unregisters that same reminder (`RunnerGrain.cs:891-892`), leaving pending/running work with no `work-timeout` reminder. That violates issue AC #1 and #4: scanning is no longer guaranteed to remain active while outstanding work exists, and timeout synthesis can stop until some later assignment happens to re-register. [disallowed:product-behavior-change]
  SuggestedAction: Make unregister conditional on the post-await state and recover after unregister if work appeared concurrently. A robust shape is: before unregistering, re-check the active work set; after `UnregisterReminder` returns, if pending/running work now exists, call `EnsureWorkTimeoutReminderAsync` again. Add a reentrant lifecycle regression test that orchestrates drain/unregister interleaving with a new assignment and verifies a reminder remains registered for the new outstanding work.
  Verification: Add and run a targeted server test covering the race, then run `dotnet test Mohist.sln --filter FullyQualifiedName~RunnerWorkLedgerSpecs -p:SkipWebBuild=true` and `npm test`.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain/RunnerWorkLedgerSpecs.cs`
  Evidence: The added lifecycle tests cover normal first-register, no-reset-on-second-assignment, drain-by-report unregister, drain-plus-new-work re-register, and a timer-driven older-work timeout (`RunnerWorkLedgerSpecs.cs:378-525`). They do not cover the reentrant drain/new-assignment interleaving described in item-1. The closest test, `EnsureWorkTimeoutReminder_ReregistersWithFreshStartAt_AfterDrainAndNewWork`, assigns new work only after the report path has fully unregistered the old reminder (`RunnerWorkLedgerSpecs.cs:457-472`), so it cannot catch the candidate's exposed race window. [disallowed:broad-test-design]
  SuggestedAction: Add a deterministic test seam or fake reminder table/service that can pause `UnregisterReminder` after `GetReminder` has returned, issue a new assignment while the old reminder is still visible, then release the unregister and assert the runner still has a `work-timeout` reminder for the outstanding work.
  Verification: The new regression test should fail on the current candidate and pass after the lifecycle fix. Then run the targeted runner grain tests and the full `npm test` gate.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: verification
  Evidence: `npm test` passed in this snapshot. The server test summary was `Failed: 0, Passed: 3067, Skipped: 13, Total: 3080` for `Mohist.Server.Tests.dll`; the runner workspace summary was `53 passed | 3 skipped` files and `782 passed | 23 skipped` tests. `dotnet test Mohist.sln --filter FullyQualifiedName~RunnerWorkLedgerSpecs -p:SkipWebBuild=true` also passed: `19` tests, `0` failed. `git diff --check master...HEAD` produced no output.
  SuggestedAction: Keep these commands in the final verification set after fixing item-1.
  Status: out-of-scope

<promise>FAIL</promise>
