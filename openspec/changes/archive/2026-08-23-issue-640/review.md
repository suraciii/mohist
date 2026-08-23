# Review: issue 640

## Verdict

PASS — the previous must-fix test-coverage gap is resolved, the repair introduced no must-fix regression, and the change is ready to merge.

## Previous finding disposition

### MF-1 — RESOLVED

**Criterion rechecked:** `openspec/changes/issue-640/tasks.json` T-002 acceptance criterion 8 requires coverage for both OpenCode and Pi at cleanup attempt 1 and attempt 2+, including a retained prior-cleanup `session-followup` fact after the Workflow boundary settles, already-delivered predecessors, admission ordering, timeout evidence, manifest validation, bounded-attempt accounting, actual cleanup outcomes, and preserved fail-closed behavior.

The repair now supplies the missing coverage:

- `packages/runner/tests/cleanup-turn-admission.spec.ts:291-382` drives the production outbox through OpenCode and Pi attempt-1 admission and proves the session is not opened and the cleanup turn is not submitted until the retained original-turn terminal fact settles durably.
- `packages/runner/tests/cleanup-turn-admission.spec.ts:384-439` covers both runtimes at attempt 2 with the prior Workflow cleanup boundary already removed while a correlated Session-scoped terminal fact remains retained. Both paths stay closed until that fact settles.
- `packages/runner/tests/cleanup-turn-admission.spec.ts:441-468` verifies immediate admission for already-delivered predecessors through the production outbox.
- `packages/runner/tests/cleanup-turn-admission.spec.ts:470-515` verifies production-outbox timeout mapping and structured session/work/budget evidence for both runtimes.
- `packages/runner/tests/cleanup-turn-admission.spec.ts:517-551` preserves cross-attempt fail-closed behavior and verifies that both built-in manifests declare and preserve `session-delivery-wait-timeout`.
- `packages/runner/tests/worktree-cleanup-delivery.spec.ts:405-533` demonstrates that all three bounded cleanup attempts remain usable under predecessor-delivery lag, use the correct immediate predecessor identity, retain attempt accounting, and complete according to the third cleanup's actual worktree result. The adjacent existing failure case at `packages/runner/tests/worktree-cleanup-delivery.spec.ts:354-403` continues to verify failure after the bounded attempts leave the worktree dirty.

The new production-path tests also exposed an input-receipt settlement race. The repair in `packages/runner/src/server/runtime-event-outbox.ts:346-357` joins the in-flight snapshot-write chain and rechecks the receipt cache before treating an already-removed cleanup input as receiptless. This is consistent with durable settlement ordering and the full Runner suite found no regression.

## Re-review checks

- **Previous findings:** Checked. MF-1 is fully addressed; there is no remaining must-fix finding or unsupported won't-fix disposition.
- **Regression check:** Checked. The only production-code repair after the prior review is the input-receipt race handling above. It preserves durable removal semantics and existing terminal-error behavior; focused outbox, cleanup admission, and worktree tests pass.
- **New must-fix problems:** None found in the repair. No pre-existing issue missed by the previous per-dimension review meets the must-fix bar.

## Acceptance and dimension verification

- **Issue criteria:** Re-read before reviewing the repair. The Mohist issue record has no body criteria; the governing acceptance criteria are the specs and `tasks.json` under `openspec/changes/issue-640/`.
- **Coverage:** Checked, no issue. The repaired tests cover the previously missing runtime/scenario matrix and bounded cleanup-loop behavior.
- **Correctness:** Checked adversarially, no issue. Production-outbox tests prove wait-before-open/submission ordering for both runtimes, later-attempt Session correlation after Workflow-boundary settlement, immediate completion, and structured timeout behavior.
- **Consistency:** Checked, no issue. The race repair follows the outbox's serialized snapshot-write and receipt-cache patterns without changing delivery keys, acknowledgement policy, batching, retention, or server contracts.
- **Tests:** Checked, no issue. Focused and full Runner verification pass.

## Verification performed

- `npm --prefix packages/runner exec vitest run tests/cleanup-turn-admission.spec.ts tests/runtime-event-outbox-delivery-wait.spec.ts tests/worktree-cleanup-delivery.spec.ts tests/runtime-event-outbox.spec.ts tests/runtime-event-outbox-cleanup.spec.ts` — passed: 5 files, 51 tests.
- `npm --prefix packages/runner run typecheck:tests` — passed.
- `npm --prefix packages/runner run build` — passed.
- `npm --prefix packages/runner run test:ci` — passed: 160 files, 1746 tests.
- `npm run format:check` — passed.
- `npm run check:filesizes` — passed.
- `git diff --check origin/master...HEAD` — passed.

## Observations

- `awaitCleanupPredecessorDelivery` remains optional on the outbox port for lightweight test doubles. This was an explicit plan decision; the production implementation and repaired production-path admission tests prevent it from weakening this change's delivered behavior.

<promise>PASS</promise>