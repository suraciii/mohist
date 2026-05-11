## Context

`BaseStageRunner.runChecksPhase()` currently stops on the first non-`pass` result, persists partial results, and immediately dispatches failure handling. That behavior keeps the control flow simple, but it hides later failures in the same phase and turns stage repair into a serial discover-fix-rerun loop.

The current workflow model already has the right boundaries for this change: checks are read-only validators, repairs happen through explicit stage tasks selected by `CheckFailurePolicy`, and `UserApprovalCheck` only reads `approvalState`. The change should stay inside shared check-phase orchestration in `base-stage-runner.ts` so plan/build/check runners keep their existing business policies and check lists.

## Goals / Non-Goals

**Goals:**

- Run all checks in the current phase once before deciding how to handle failures.
- Preserve `Check` as a read-only interface and keep approval request creation outside checks.
- Treat `user-approval` pending as a local awaiting-approval outcome, not as a repair target.
- Repair only failed or errored non-approval checks that have an existing `CheckFailurePolicy`.
- Preserve the existing semantic that a successful fix can change stage state and later checks should run against that updated state.
- Keep complete check visibility and fix task visibility in existing persisted results.

**Non-Goals:**

- Do not redesign stage-specific checks, check names, or stage runner business logic.
- Do not introduce a new reaction/fallback/escalation system.
- Do not make checks write approval state, create approval requests, or spawn repair work directly.
- Do not change the storage model for check results or task results beyond what existing persistence already supports.

## Decisions

### D1: Split check-phase orchestration into collect, classify, and repair steps

`runChecksPhase()` should move from a single streaming loop with inline failure exit to a three-step orchestration:

1. Run each check in order and collect one initial result per check.
2. Classify the collected results into:
   - passing checks
   - pending approval checks
   - failed or errored non-approval checks
   - unrecoverable failed checks with no policy
3. Decide the next action from the full phase picture.

This keeps the runner interface shallow: stage runners still supply ordered checks and failure policies, while `BaseStageRunner` owns the orchestration complexity.

**Alternatives considered:** Keep the current streaming loop and add special cases to continue after some failures. That would mix collection and recovery logic in one path and make approval/fix ordering harder to reason about.

### D2: Use one initial full pass as the diagnostic baseline

The first pass over a phase should always attempt every check in order, even if earlier checks fail. This produces the complete health snapshot the user needs before any repair starts.

The runner should persist these baseline results before launching repairs so stage history can show both the original failures and later rechecks. The design does not require a separate storage schema; the existing `checkResults` list already supports multiple entries for the same check name, and callers that need the current truth can continue to use latest-result selection helpers such as `getLatestCheckResult()`.

**Alternatives considered:** Delay persistence until all repairs finish. That would hide the original phase diagnosis and make partial repair failures harder to explain.

### D3: Approval remains a terminal phase outcome, not a repair candidate

`UserApprovalCheck` keeps its existing read-only contract:

- `approved` -> `pass`
- `awaiting` or missing -> `pending`
- `rejected` -> `fail`

During post-collection classification, `pending` approval is handled separately from ordinary failures. If the phase has no failed or errored non-approval checks and contains a pending approval result, the runner should enter the existing approval handling path and return an awaiting-approval result. It should not attempt repair for that check.

This preserves the important boundary that checks observe approval state, while runners decide when to request or refresh approval.

**Alternatives considered:** Treat pending approval as just another non-pass result and let the generic failure path handle it. That would blur approval semantics and create pressure to encode approval side effects in checks.

### D4: Repairs are driven from the collected failure set, one repair target at a time

After the initial collection pass, the runner should inspect failed or errored non-approval checks in check order and choose the first one with a matching `CheckFailurePolicy` as the active repair target. Repair then follows the existing contract:

1. Run the configured fix task.
2. Append the fix task result.
3. Re-run the repaired check.
4. If the check passes, continue running later checks from that point using fresh state.
5. If the check still fails and attempts remain, retry according to the existing policy.
6. If attempts are exhausted, stop in the current stage with visible evidence.

Choosing one repair target at a time preserves current sequencing semantics and avoids overlapping repairs whose changes might invalidate one another. The full collected baseline still gives the user visibility into all initially failing checks.

**Alternatives considered:** Launch repair for every failed check from the baseline set before any recheck. That would maximize concurrency of failure handling, but it would violate the current assumption that a fix may change later check state and that later checks should be re-evaluated after earlier repairs.

### D5: Successful repair resumes from the repaired check, not from the beginning

The current `runFixAndRecheck()` behavior already encodes an important optimization and semantic guarantee: after a fix passes its recheck, the runner continues with remaining checks rather than rerunning the whole phase from scratch. The new design should keep that behavior.

What changes is only the entry point into repair. Instead of entering repair on the first failing result discovered during the initial pass, the runner enters repair after collecting the full baseline and selecting the first repairable failure in order.

This preserves compatibility with stage-specific logic that assumes repairs can unlock or change downstream checks without discarding all prior successful results.

**Alternatives considered:** After every successful repair, rerun the entire phase from the first check. That is simpler to describe, but it can repeat expensive checks unnecessarily and changes existing ordering semantics more than this issue requires.

### D6: Unrepairable failures still stop the stage locally with complete evidence

If the collected baseline contains a failed or errored non-approval check without a matching policy, the stage should fail after persisting the full collected results. If a repairable failure exists earlier in check order, that failure is repaired first; later unrepairable failures remain visible in the baseline and can become the stop reason if reached after repair continuation.

This aligns with the existing no-fallback policy: the stage remains in place with visible failure evidence and explicit fix task history when available.

**Alternatives considered:** Ignore unrepairable failures when some other failure is repairable. That would risk advancing or requesting approval while known failures are already visible.

## Risks / Trade-offs

- [Risk] Appending baseline results plus recheck results can create longer result histories with duplicate check names. → Keep baseline persistence for diagnostic value and rely on latest-result helpers for current truth.
- [Risk] Continuing the initial pass after an early failure may run checks against a state that is already known to be broken. → Accept this intentionally because the goal is diagnostic completeness; later repair continuation still reruns downstream checks against repaired state.
- [Risk] Repair target selection could become ambiguous when multiple checks fail and multiple policies exist. → Define a strict rule: choose the first failed or errored non-approval check in declared check order that has a policy.
- [Risk] Approval handling can accidentally run while ordinary failures still exist. → Gate approval handling on the absence of failed or errored non-approval checks in the current effective result set.
- [Risk] The shared runner may become harder to read if collection and repair logic stay in one method. → Factor the implementation into small helpers such as `collectCheckResults`, `classifyPhaseResults`, and `resolveCollectedFailures`.

## Migration Plan

1. Refactor `BaseStageRunner` check execution into small internal helpers for collection, classification, and repair dispatch.
2. Change `runChecksPhase()` to perform a full ordered collection pass and persist the baseline results.
3. Add result classification that distinguishes approval-pending from failed or errored non-approval checks.
4. Reuse existing `runFixAndRecheck()` semantics for the selected repair target, keeping fix task persistence and downstream continuation behavior.
5. Keep `handleApprovalCheck()` as the only place that writes awaiting approval state, but invoke it only after collected results show no ordinary failures.
6. Verify stage status updates still map to `awaiting-approval` only when the effective result set ends in approval pending, otherwise `failed`.
7. Add regression tests for multi-failure collection, approval pending without repair, repairable failure plus later failure visibility, and fix-then-continue ordering.

Rollback is low risk because the change is isolated to shared runner orchestration. Reverting to first-failure short-circuit only requires restoring the old `runChecksPhase()` flow; no data migration is involved.

## Open Questions

- Should a rejected `user-approval` result continue to use the ordinary `fail` status and current message text, or should the runner attach a more explicit non-repairable rejection reason when it becomes the terminal outcome after full-phase collection?
