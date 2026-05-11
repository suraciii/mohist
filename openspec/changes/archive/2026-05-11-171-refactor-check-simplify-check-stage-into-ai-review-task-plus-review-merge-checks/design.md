## Context

The current check stage mixes user-facing checks with internal execution details. `CheckStageRunner` runs `health:check`, `merge-readiness`, and `integration-health-gate-preview` as pre-task checks, then generates `review.md` and `review-self-check.md`, then runs `ai-review` as a check before `user-approval`. `BaseStageRunner` also gives `ai-review` special treatment by persisting it as the authoritative review truth and tying approval to its snapshot.

The desired model keeps the existing stage runner, stage execution, check suite, and approval snapshot infrastructure, but moves the public contract up one level: `ai-review` becomes the task that produces a valid final review artifact for the current code snapshot, while `review-passed` and `merge-ready` are the only automated user-visible checks before `user-approval`.

## Goals / Non-Goals

**Goals:**

- Make check-stage task history show `ai-review` as the initial visible task.
- Make check-stage visible checks exactly `review-passed`, `merge-ready`, and `user-approval`.
- Treat missing, malformed, or unparsable `review.md` as `ai-review` task failure, not a separate check failure.
- Preserve review truth convergence: approval is only requested for the current `HEAD` snapshot and its final PASS review.
- Dynamically create repair work only when `review-passed` finds failing review findings.
- Re-run `ai-review` whenever merge-readiness work changes the candidate snapshot.
- Hide health gates and integration preview details as internal task/check evidence rather than user-facing checks.

**Non-Goals:**

- No full stage-state or database model rewrite.
- No changes to integrate-stage spec sync, archive, merge, or final-health responsibilities.
- No broad fallback/recovery policy matrix.
- No new public checks for review artifact validation, health evidence, or integration preview metadata.
- No new external runtime dependencies.

## Decisions

### D1: Make `ai-review` a composite check-stage task

`CheckStageRunner.executeTasks` should collapse the current `review` and `review-self-check` task sequence into one visible `ai-review` task result. Internally, that task may run health verification, generate `review.md`, run self-check/retry prompts, auto-repair simple review findings, and regenerate review artifacts, but the task contract is simple: it completes only when the final `review.md` exists, has the expected format, contains a parseable verdict, and describes the current code snapshot.

Implementation-wise, keep most of the existing agent-session artifact generation flow, but wrap it behind a single task id/title (`ai-review`). Internal rounds can remain in logs/session events as evidence, but stage task updates and persisted task results should not expose `review`, `review-self-check`, or empty predeclared fix tasks as first-class user work.

**Alternatives considered:** Keep `review` and `review-self-check` as separate visible tasks. Rejected because the product model wants the user to understand one review task, not the internal artifact generation protocol.

### D2: Replace `AiReviewCheck` with `ReviewPassedCheck`

The existing `AiReviewCheck` already does the right verification shape: read `review.md`, parse the verdict, and return PASS/FAIL evidence. Its user-facing name and error boundary are wrong. Rename or replace it with `ReviewPassedCheck` whose `name` is `review-passed` and whose error cases are reserved for situations that should not happen after a successful `ai-review` task.

Missing artifact, malformed format, or unparsable verdict should be detected during the `ai-review` task before checks run. If those conditions are still observed by `review-passed`, treat them as an orchestration invariant violation and fail the stage with a message that points back to rerunning `ai-review`, but do not introduce another visible check.

**Alternatives considered:** Keep the check named `ai-review` and only relabel it in the UI/API. Rejected because the backend state would still encode the wrong task/check boundary and future callers would continue depending on the ambiguous name.

### D3: Dynamic review repair is driven by `review-passed` failure

Remove the static failure policy that maps `ai-review` to `fix-review-findings`. Instead, when `review-passed` returns a FAIL verdict with extracted findings/fix suggestions, the stage runner creates and runs an actual repair task at that moment. The repair task should reuse `runReviewFixTask`, but its task id/title should be generated from the failure context, for example `repair-review-findings` or `repair-review-findings-<attempt>`.

After the repair task completes, invalidate the existing review artifacts/checkpoint and rerun the full `ai-review` task before re-running `review-passed`. This preserves the rule that final `review.md` always describes the post-repair code.

**Alternatives considered:** Predefine a pending `fix-review-findings` task in check-suite state. Rejected because it creates empty work that may never happen and violates the principle that tasks represent work actually assigned to an agent.

### D4: Make `merge-ready` responsible for mergeability and snapshot invalidation

Replace the user-visible `merge-readiness` check with `merge-ready`. The check should answer whether the reviewed candidate can be integrated into the target branch. If the candidate is already mergeable, it passes with target branch and snapshot evidence. If it needs rebase or conflict handling, the failure policy may run concrete merge-readiness work: rebase, conflict-resolution task, or block with conflict evidence.

Any merge-readiness work that changes `HEAD` must reset review truth for the active check suite: mark `ai-review` task/review evidence stale, reset `review-passed` to pending, update the suite snapshot, and rerun `ai-review` before approval can be requested. If merge-readiness only gathers evidence and does not change code, the existing review remains valid.

**Alternatives considered:** Keep merge readiness before AI review as a pre-task check. Rejected because merge-readiness may rebase or otherwise change code, which would invalidate any later claim that approval is based on a reviewed snapshot unless the orchestration explicitly loops back through review.

### D5: Keep health and integration preview as internal evidence

`health:check` and `integration-health-gate-preview` should no longer be in the user-visible check list. Health verification can run inside the `ai-review` task before review generation, with failures causing the task to fail or run internal health repair. Integration health policy preview can be attached to task/check output or done evidence where useful, but it should not block or appear as a separate check-stage decision point.

This pulls implementation complexity downward: users see the meaningful decision points, while diagnostic details remain available in logs, task output, and done evidence.

**Alternatives considered:** Keep health and integration preview as visible checks with friendlier labels. Rejected because friendlier labels still ask users to reason about internal mechanics that are not independent approval decisions.

### D6: Re-key check-suite state to the simplified public model

Update `CheckSuiteChecks` and `CheckSuiteRepo.makeInitialChecks()` from `build-test`, `ai-review`, `user-approval` to `review-passed`, `merge-ready`, `user-approval`. The `ai-review` task result should live in stage execution task history, not in check-suite checks. API approval validation should read the latest `review-passed` result and approval snapshot rather than a latest `ai-review` check result.

For compatibility with existing persisted suites, read paths may tolerate old keys by mapping legacy `ai-review` passed state to `review-passed` when presenting old executions, but new check-suite writes should only use the simplified keys.

**Alternatives considered:** Add `review-passed` and keep `ai-review` in the check suite for compatibility. Rejected because it keeps the confusing public model and makes approval logic choose between duplicate review states.

### D7: API and UI consume the same domain names as the runner

`GET /api/issues/:number`, `GET /api/issues/:number/check-suite`, stage execution responses, SSE `check_update` events, CLI rendering, and Web UI types/hooks should use `review-passed`, `merge-ready`, and `user-approval` as visible check names. UI labels can be friendly (`Review passed`, `Merge ready`, `Approval`), but should not special-case hidden internal names.

Done evidence can still display merge and final-health evidence after integration, but check-stage readiness panels should stop requiring `merge-readiness` or `integration-health-gate-preview` check records.

**Alternatives considered:** Preserve old API names and transform them only in UI components. Rejected because API consumers and approval validation would still observe the old model.

## Risks / Trade-offs

- [Risk] Existing tests and persisted data expect `ai-review` as a check key. → Mitigation: update regression tests to assert task/check separation and add read-side tolerance for legacy suites/executions where low-cost.
- [Risk] Moving health verification inside `ai-review` could make failures feel less specific. → Mitigation: include health command summary and log excerpts in `ai-review` task output and session logs while keeping the visible task failed.
- [Risk] Dynamic repair loops can become hard to reason about if both review repair and merge repair mutate code. → Mitigation: use one explicit invalidation path: any code-changing repair clears review artifacts/checkpoints and restarts from `ai-review`.
- [Risk] Approval could accidentally use stale review output if check-suite snapshot and stage execution results diverge. → Mitigation: keep the existing `snapshotSha` convergence checks, but rebase them around `review-passed` output and the approval snapshot.
- [Risk] Renaming public check keys may briefly break Web UI optimistic updates. → Mitigation: update backend event names, frontend `CheckSuiteChecks` types, default state, and reset logic in one change.

## Migration Plan

1. Add `ReviewPassedCheck` and `MergeReadyCheck` names while preserving the underlying parsing and mergeability evidence shape.
2. Refactor `CheckStageRunner` default flow to run a single visible `ai-review` task, then `review-passed`, `merge-ready`, and `user-approval` checks.
3. Move review artifact validation and retry handling into the `ai-review` task failure path.
4. Replace static `ai-review` check failure policy with dynamic review repair from `review-passed` failure.
5. Add a merge-ready repair/invalidation path that reruns `ai-review` whenever merge work changes `HEAD`.
6. Update check-suite types/defaults, stage-context helpers, approval API validation, CLI output, SSE consumers, and Web UI types/hooks to use the simplified names.
7. Update tests that assert check order, review convergence, approval gating, and UI/API check-suite shape.

Rollback is code-level: revert the runner/check-suite/API/UI changes together. No schema migration is required if the check-suite `checks` JSON remains schemaless; new code should tolerate older JSON keys on read.

## Open Questions

- What exact task id should dynamic review repair use when multiple repair attempts occur: stable `repair-review-findings` with attempts, or attempt-suffixed ids for easier timeline reading?
- Should health verification run at the start of `ai-review` every time, or only before the first review generation and after code-changing repair?
