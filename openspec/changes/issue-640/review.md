# Review: issue 640

## Verdict

FAIL — the implementation appears consistent with the requested admission behavior, but the required regression coverage is incomplete.

## Must-fix findings

### MF-1 — Required runtime admission scenarios are not covered by the implemented tests

**Violated criterion:** `openspec/changes/issue-640/tasks.json` T-002 acceptance criterion 8 requires Runner tests for **both OpenCode and Pi**, for cleanup attempt 1 and attempt 2+, including a retained prior-cleanup `session-followup` fact after the Workflow-keyed boundary has settled, already-delivered facts, ordering before open/submission, timeout evidence, manifest validation, maximum-attempt accounting, actual cleanup success/failure, and preserved cross-attempt fail-closed behavior.

The new admission suite does not provide that required matrix:

- `packages/runner/tests/cleanup-turn-admission.spec.ts:117-229` covers OpenCode attempt 1, attempt 2 target derivation, one OpenCode timeout, and non-cleanup fail-closed behavior, but all delivery waits are mocked. It does not exercise admission against the production outbox with retained predecessor records.
- `packages/runner/tests/cleanup-turn-admission.spec.ts:256-317` covers only Pi attempt 2 with a mocked wait. There is no Pi attempt-1 delivery-lag test and no Pi timeout/evidence test.
- `packages/runner/tests/runtime-event-outbox-delivery-wait.spec.ts:85-179` proves the outbox primitive can remain pending on a correlated `session-followup` record, but it is not connected to either runtime admission path. Consequently, no test proves that OpenCode or Pi actually remains unopened while that real retained record is pending and proceeds only after its durable settlement.
- There is no runtime-admission test using the real outbox for the already-delivered immediate path, and no changed-behavior test demonstrating bounded attempt accounting and actual cleanup success/failure while predecessor delivery is delayed.

Add the missing regression coverage, preferably by driving the production outbox through the OpenCode and Pi admission paths. At minimum, cover Pi attempt 1, both runtimes at attempt 2+ with the cleanup boundary already settled but a correlated Session-scoped terminal fact retained, both runtime timeout mappings, and the required cleanup-result/attempt-accounting behavior under delivery lag.

## Dimension checks

- **Issue criteria re-read before diff:** Checked. The issue record itself has no body criteria; the concrete acceptance criteria are in `openspec/changes/issue-640/specs/` and `tasks.json`.
- **Coverage:** Checked. The production code covers both runtime admission sites, immediate and later predecessor identities, structured timeout propagation, and preservation of the non-cleanup fail-closed guard. Test coverage is incomplete as described in MF-1.
- **Correctness:** Checked adversarially. No separate must-fix implementation defect found in predecessor correlation, durable-settlement waiter resolution, admission ordering, timeout evidence, or worktree failure preservation.
- **Consistency:** Checked. The change follows the existing outbox timer, snapshot settlement, reporter correlation, action manifest, and executor dependency patterns. No issue.
- **Tests:** Must-fix issue MF-1. The available focused tests pass, but they do not satisfy the explicitly required runtime/scenario matrix.

## Verification performed

- `npm --prefix packages/runner run test:ci` — passed: 160 files, 1741 tests.
- `npm run format:check` — passed.
- `npm run check:filesizes` — passed.
- `git diff --check` — passed.

## Observations

- `awaitCleanupPredecessorDelivery` remains optional on the port, so lightweight test doubles can omit it. The production outbox implements it, and this matches the plan; this does not affect the verdict.

<promise>FAIL</promise>