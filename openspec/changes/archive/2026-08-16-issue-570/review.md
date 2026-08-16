# Issue 570 Review

## Verdict

**PASS** — the only must-fix finding from the previous review is fixed properly; the current change is ready to merge.

## Disposition Verification

### M-1 — fixed properly: fallback RSS containment now observes production process memory

The previous review found that the no-`prlimit` path could never detect a real memory breach because the Linux `/proc` parser used escaped regexes that matched literal backslashes. That finding is resolved in [packages/runner/src/system/process.ts:414](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/packages/runner/src/system/process.ts:414) and [packages/runner/src/system/process.ts:421](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/packages/runner/src/system/process.ts:421): the `VmRSS` expression now captures digits and the child-PID list now splits on whitespace. A direct probe of the exported production reader against the current process returned a positive RSS value.

The fix also adds a Darwin `ps`-based process-tree reader at [packages/runner/src/system/process.ts:430](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/packages/runner/src/system/process.ts:430), preserving watchdog-only fallback behavior on hosts without `prlimit`. The new integration case at [packages/runner/tests/integration/resource-containment.spec.ts:39](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/packages/runner/tests/integration/resource-containment.spec.ts:39) forces `prlimit` off, disables the wall-clock bound, runs a real memory-burning child, and verifies the production RSS path reports `resource-containment`. This directly closes the previous test gap.

## Regression Check

The fix is limited to the RSS-reader implementation and its forced-fallback integration coverage. The existing Linux `prlimit` path, process-group termination behavior, and injected watchdog unit seam are unchanged. The prior review's other acceptance-criterion checks remain applicable; no new must-fix regression or unresolved prior finding was found.

## Dimension Verdicts

- **Acceptance coverage: checked, no issue.** Runner-loss closeout, AgentJob recovery, identity-preserving reconnect, deadline settlement, late-report idempotency, status projections, bounded runtime teardown, sibling preservation, and resource fallback containment are represented by the implementation and tests reviewed across this change. The previously incomplete fallback-host resource criterion is now covered.
- **Correctness: checked, no issue.** The corrected Linux parser returns live process-tree RSS, the forced fallback terminates the runaway child without terminating the runner, and the contained action maps to the required `resource-containment` result.
- **Codebase consistency: checked, no issue.** The change uses the existing external-process policy, runner resource context, process-group kill, and test seams; it introduces no unrelated product changes.
- **Tests: checked, no issue.** `npm run test:run -w packages/runner -- tests/process.test.ts` passed 4/4; the resource-containment integration suite passed 3/3; `npm run test:ci -w packages/runner` passed 153 files and 1,659 tests; runner test typecheck and test-boundary checks passed.

## Observations

- [docs/runner.md:116](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/docs/runner.md:116), [docs/the-workflow.md:178](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/docs/the-workflow.md:178), and [docs/troubleshooting.md:136](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/docs/troubleshooting.md:136) still describe active work as terminally failing with `runner-lost` and requiring explicit retry. This is documentation drift, but the issue's explicit status-surface acceptance names Web, CLI, and issue attention, so it is not a must-fix finding for this change.
- The Darwin RSS reader has no platform-native test in this Linux verification environment. The implementation is isolated behind the existing reader seam; the Linux production fallback is covered by the new integration test.
- T-008's plan artifact calls for one integration test spanning the runner journal and server report contract. Current evidence remains split between runner-host tests and server specs; deterministic coverage of the individual journal and report contracts is present, so this remains an observation rather than a must-fix finding.

<promise>PASS</promise>
