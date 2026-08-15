# Issue 570 Review

The issue acceptance criteria were re-read from `mo issue view 570` before reviewing the diff. This is the first review of the current change; there was no prior `review.md` to disposition.

## Must-fix Findings

### M-1: The no-`prlimit` memory containment path never observes production RSS

**Criterion violated:** the issue requires per-work resource runaway protection so a work that exceeds its bound is terminated without killing the Runner or cascading to siblings. The resource-isolation spec's memory-bound scenario and its `prlimit`-unavailable fallback scenario require the same behavior on hosts without `prlimit`.

In [packages/runner/src/system/process.ts:411](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/packages/runner/src/system/process.ts:411), `readProcessTreeRssBytes` uses `/(\\d+)/` in a regex literal, and line 418 uses `/\\s+/`. Those patterns match a literal backslash followed by `d`/`s`, not digits or whitespace. Consequently a real `/proc/<pid>/status` `VmRSS: ... kB` line is never parsed and child PID lists are not split. The function returns `null` for a live process (verified directly against the current source), so `startResourceWatchdog` never calls `onContainment` for an actual memory breach. On macOS and any Linux host without `prlimit`, the memory bound is therefore ineffective; the default one-hour wall-clock bound is not a memory-containment substitute and the runaway can still take down the Runner and sibling work before it fires.

The fallback unit test does not catch this because [packages/runner/tests/process.test.ts:39](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/packages/runner/tests/process.test.ts:39) injects a fake RSS reader. The integration test calls `probePrlimit()` and therefore normally exercises the Linux `prlimit` path rather than the real fallback parser. Fix the parser and add or adjust a real forced-fallback integration test so a memory-burning child is terminated by production RSS sampling.

## Dimension Verdicts

- **Acceptance coverage: FAIL.** The runner-loss closeout, AgentJob recovering projection, original-identity redelivery, deadline fallback, stale report handling, status projections, bounded runtime teardown, and sibling-preservation paths are represented in the implementation and focused tests. The resource-containment acceptance is incomplete because M-1 breaks the fallback host path.
- **Correctness: FAIL.** M-1 is a production correctness failure in the containment implementation. I checked the surrounding server interruption/deadline ownership, reconnect desired sets, journal fence, report acknowledgement, runtime drain, and status-surface mappings and found no additional must-fix problem.
- **Codebase consistency: checked, no additional issue.** The change follows the existing nullable persisted-fact and `Accepted`/`Stale` reconciliation patterns; no unrelated convention problem affects the issue criteria.
- **Tests: FAIL for the affected criterion.** `npm --prefix packages/runner run test:ci` passed with 153 files and 1,659 tests, and the server unit invocation passed 2,668 tests. Those gates do not validate the real fallback RSS parser: the fallback test uses an injected reader and the integration test normally uses `prlimit`. The server SpecTest filter was ignored by the repository's Microsoft Testing Platform wrapper and its broad run exceeded the 180-second review timeout, so no pass result is claimed for that command.

## Observations

- [docs/runner.md:116](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/docs/runner.md:116), [docs/the-workflow.md:178](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/docs/the-workflow.md:178), and [docs/troubleshooting.md:136](/home/szf/.mohist/projects/workspaces/wr_0051e4328f184ff8bbc63b6d8b538bad/docs/troubleshooting.md:136) still describe active work as terminally failing with `runner-lost` and requiring explicit retry. This contradicts the new runtime behavior and should be updated, but the issue's explicit status-surface acceptance names Web, CLI, and issue attention, so it does not add a separate must-fix finding here.
- T-008's plan artifact calls for an integration test spanning the runner journal and server report contract. The current evidence is split between runner-host tests and server specs; I treat the missing single cross-boundary test as an observation because deterministic coverage of the individual report and journal contracts is present.

<promise>FAIL</promise>
