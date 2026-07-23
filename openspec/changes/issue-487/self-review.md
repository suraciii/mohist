# Self Review

## Findings

1. **[High] T-002 omits the shared integrate merge path and leaves a now-invalid regression test.** `waitForGitHubPrChecks` is shared by `mohist/github-pr-checks` and `mohist/merge-github-pr`, so returning `unavailable` for a permanently empty rollup changes both actions. `packages/runner/tests/merge-github-pr.spec.ts` currently asserts that an empty rollup after the grace window proceeds to merge. T-002 only requires coverage for the checks action and does not require updating that merge specification or asserting that integrate surfaces `pr-checks-unavailable` without issuing a merge command. Add the affected merge behavior and test update to T-002 so the runner suite can pass and integrate's final protection is verified under the new policy.

2. **[High] T-002 does not specify deterministic time control for its bounded-polling tests.** The task requires only "injected timing and fake gh responses." The polling implementation uses `Date.now()` and timer delays, while `design/testing.md` requires runner tests to use `vi.useFakeTimers` or an injected `now` source and forbids real-time waits. Require fake timers with deterministic advancement, or inject a clock/sleeper seam, for empty-to-passing and permanently-empty scenarios; restore the test state afterward.

<promise>FAIL</promise>
