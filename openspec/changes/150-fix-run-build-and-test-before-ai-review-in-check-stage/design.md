## Context

`CheckStageRunner` currently creates `review.md` and `review-self-check.md` inside `executeTasks()`. `BaseStageRunner.run()` always calls `executeTasks()` before running checks, so the configured default check order of `BuildTestCheck`, `AiReviewCheck`, and `UserApprovalCheck` does not actually make build/test the first check-stage operation.

The change must preserve the existing check model, `checks.buildTest` configuration, AI review behavior, approval handling, and stage execution reporting while making build/test a fail-fast prerequisite for review artifact generation.

## Goals / Non-Goals

**Goals:**

- Run `BuildTestCheck` before `CheckStageRunner` generates or reuses AI review artifacts.
- Reuse the existing build/test command, timeout, result shape, and autofix behavior.
- Stop the check stage after exhausted build/test autofix attempts without generating review artifacts or requesting approval.
- Continue to generate review artifacts, run `AiReviewCheck`, and request user approval only after build/test passes.
- Keep the change local to check-stage orchestration and shared runner mechanics needed to support it.

**Non-Goals:**

- Do not introduce build/test gates for plan, build, done, or every workflow stage.
- Do not redesign the check suite UI or stage execution persistence model.
- Do not change merge or post-merge verification behavior.
- Do not change the `checks.buildTest` workflow configuration schema.
- Do not require a second build/test pass after AI review autofix in this change.

## Decisions

### D1: Add an explicit pre-task check phase to stage orchestration

`BaseStageRunner` should support an optional pre-task check phase that runs before `executeTasks()`. The default implementation returns no pre-task checks, so existing stages keep their current `tasks -> checks` behavior. `CheckStageRunner` will place `BuildTestCheck` in this pre-task phase and leave `AiReviewCheck` plus `UserApprovalCheck` in the post-task phase.

This keeps build/test ordering in the shared stage lifecycle instead of duplicating stage execution setup, check persistence, reaction handling, and status updates inside `CheckStageRunner`.

**Alternatives considered:** Override `CheckStageRunner.run()` and manually run build/test before calling review generation. This would avoid touching `BaseStageRunner`, but it would duplicate private orchestration logic and increase the chance that check-stage status, persistence, and approval behavior diverge from other stages.

### D2: Scope autofix continuation to the current check phase

The check runner should execute reactions within the active check list. When `BuildTestCheck` fails and autofix later passes, the runner should continue by entering `executeTasks()` rather than immediately running `AiReviewCheck` before `review.md` exists. After review artifacts are generated, the post-task phase runs `AiReviewCheck` and `UserApprovalCheck` with the existing AI review reaction behavior.

This likely means refactoring the internal check execution helper to accept a check sequence for the active phase rather than always consulting the full stage check list after an autofix succeeds.

**Alternatives considered:** Keep `BuildTestCheck` in the normal check list and also run it manually before `executeTasks()`. That would satisfy ordering but run build/test twice on the success path, making the check stage slower and producing confusing duplicate results.

### D3: Treat build/test failure as a hard precondition failure

If `BuildTestCheck` still fails after its configured autofix attempts, the check stage should return the existing failed/escalated check result and stop before review artifact generation. The failure message should come from `BuildTestCheck.message`, and `BuildTestCheck.output.buildLog` should contain a truncated log excerpt suitable for UI/API consumers.

No approval state should be set in this path because `UserApprovalCheck` is not reached.

**Alternatives considered:** Generate AI review even when build/test fails so the reviewer can comment on both semantic and mechanical issues. This preserves more feedback but produces stale or misleading review artifacts and violates the desired CI-like fail-fast behavior.

### D4: Preserve existing review artifact checkpoint behavior after build/test passes

The existing review task loop may skip generation when checkpoints or artifact files already indicate `review` or `review-self-check` completed. That behavior should remain, but it must only be evaluated after build/test has passed for the current check-stage run.

Existing stale files from an earlier run do not cause approval by themselves; approval still requires the post-task `AiReviewCheck` and `UserApprovalCheck` sequence to run after the build/test gate.

**Alternatives considered:** Delete existing `review.md` and `review-self-check.md` before every check-stage run. This would eliminate stale artifacts more aggressively, but it changes resume behavior and is not required for the ordering fix.

### D5: Defer build/test rerun after AI review autofix

If `AiReviewCheck.fix()` changes source code, the ideal follow-up is to rerun build/test before requesting approval. This design does not include that loop because it requires a broader reentrant check-suite structure: `build/test -> review -> AI autofix -> build/test -> review/approval`.

The current change establishes the required initial ordering and preserves existing AI review autofix behavior after mechanical verification passes.

**Alternatives considered:** Add a second `BuildTestCheck` after `AiReviewCheck`. This is smaller than a full loop, but it can leave `review.md` stale after the second build/test autofix and would introduce a new ordering ambiguity.

## Risks / Trade-offs

- [Risk] Refactoring `BaseStageRunner` check sequencing could affect plan-stage checks if defaults are wrong. → Keep pre-task checks opt-in with an empty default and cover existing stage behavior in tests.
- [Risk] Autofix continuation may accidentally run post-task checks before review artifacts exist. → Pass the active phase check list into reaction handling so successful autofix resumes only the current phase.
- [Risk] Build/test logs may still be too large or noisy for users. → Keep the full truncated `buildLog` for details and use the existing concise `message` summary for stage failure display.
- [Risk] Existing review artifacts from a previous run may remain on disk after a later build/test failure. → Do not request approval or run AI review on failure; treat artifact cleanup as a separate policy decision if needed.

## Migration Plan

- Refactor `BaseStageRunner` to support pre-task and post-task check phases while preserving default behavior for existing stages.
- Move default check-stage `BuildTestCheck` into the pre-task phase and keep AI review plus user approval in the post-task phase.
- Ensure build/test autofix exhaustion returns a failed check-stage result before `CheckStageRunner.executeTasks()` is called.
- Add or update tests for successful ordering, build/test autofix success, build/test autofix exhaustion, no review artifact generation on build/test failure, and no approval request before both gates pass.
- Rollback by restoring the previous single post-task check sequence and default check list if the phased runner causes regressions.
