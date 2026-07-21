# Self Review: Issue 461

## Findings

### 1. High: generic follow-up delivery is outside the FIFO used by AgentJob events

The spec promises production-order delivery per logical AgentSession (`specs/agent-session-runtime-event-delivery/spec.md:60-68`), and the design keys every generic queue by `(projectId, sessionId)` (`design.md:46`). The atomic task switches Workflow reporting and follow-up input/outcomes to the shared outbox (`tasks.json:9`, `tasks.json:25-29`) but does not migrate AgentJob transcript production.

AgentJob input and activity currently continue through an independent direct-upload chain (`packages/runner/src/runtime/agent-job-executor.ts:98-149`). A generic follow-up can therefore drain from the outbox while an earlier AgentJob event for the same AgentSession is still in flight, violating the stated FIFO and transcript-boundary guarantees.

The plan must either include AgentJob input/activity reporting in the shared ordering mechanism, or explicitly narrow the FIFO contract to source-local sequences and define the ordering boundary between AgentJob and generic follow-up producers. The current design claims one logical Session FIFO without controlling all of its producers.

### 2. High: verification omits runner test-boundary and test-typecheck guards

The task requires only `npm run typecheck -w packages/runner` and `npm test -w packages/runner` (`tasks.json:31`). The runner's `test:ci` script additionally runs `typecheck:tests` and `check:test-boundaries` before Vitest (`packages/runner/package.json:14-20`). Those guards are directly relevant because this change adds a recording filesystem, fake-time recovery, and many new runner tests while repository policy prohibits real filesystem and wall-clock dependencies.

T-001 must require `npm run test:ci -w packages/runner`, or explicitly run `npm run typecheck:tests -w packages/runner`, `npm run check:test-boundaries -w packages/runner`, and the test suite. Production typecheck plus Vitest alone does not verify the plan's stated test constraints.

## Review Summary

- The plan covers the issue's upload-failure recovery, restart recovery, and non-blocking Server-delivery requirements without adding Server deduplication or cross-runner transfer.
- Matching-receipt versus successful-response semantics, stale-binding scope, local enqueue failure dispositions, autonomous health recovery, migration, and the one-task atomic outbox replacement are now internally consistent and testable.
- The remaining blockers are the uncontrolled AgentJob producer in the claimed generic Session FIFO and incomplete verification commands for the repository's runner test rules.

## Verdict

The plan is not ready to build.

<promise>FAIL</promise>
