## Context

The workflow runner already presents stages as tasks followed by checks, but the implementation blurs the boundary. `Check` includes `fix?()`, each check exposes a `ReactionConfig`, and `BaseStageRunner` reacts to failures by either re-running `executeTasks()` or invoking `check.fix()`. Health gate and AI review checks currently spawn coder sessions from `fix()`, so the pipeline history can show a failed check while hiding the actual code-changing work that followed.

Stage execution persistence already has the right shape for the target model: `stage_executions.task_results` and `stage_executions.check_results` are separate JSON fields, and stage runners append `StageTaskResult` entries. The refactor should preserve that storage model, tighten the type semantics, and move fix behavior into explicit task execution paths so UI and APIs can show a clear audit trail.

## Goals / Non-Goals

**Goals:**

- Make `Check` a read-only interface with `name` and `run(ctx)` only.
- Add `StageTaskResult.output` for transient task execution details while keeping `artifacts` limited to durable workflow files.
- Replace check-owned retry/auto-fix reactions with stage-local check failure policies that map a failed check to an explicit fix task and max attempts.
- Move health gate, review finding, and plan artifact repair behavior into named tasks that append task results and emit normal task updates.
- Preserve the existing plan -> build -> check -> integrate stage flow where possible.
- Keep failed check evidence and fix task results visible when max attempts are exhausted.

**Non-Goals:**

- No fallback chain, nested reaction chain, fallback-to-plan/build, or fallback ask-user policy is introduced in this issue.
- No new durable artifact category is introduced for build logs, command output, agent streams, or health gate evidence.
- No database schema migration is required unless implementation discovers consumers that cannot tolerate the optional `StageTaskResult.output` field.
- No broad redesign of `workflow.yaml`; legacy health gate `autoFix` / `maxFixAttempts` can be translated internally during migration.
- No attempt to make shell-based checks side-effect-free beyond treating them as verification commands; they must not intentionally write workflow artifacts, modify code, or spawn fix agents.

## Decisions

### D1: Keep `BaseStageRunner` as the orchestration boundary

`BaseStageRunner` should own the generic loop: run pre-task checks, run stage tasks, run post-task checks, and process a failed check through a policy lookup. Subclasses provide checks, main task execution, and fix task implementations through narrow hooks rather than exposing reaction behavior on each check.

The target loop is:

```text
run pre-task checks
run stage tasks
run post-task checks
if check passes -> continue
if check fails and policy exists -> run fix task, then re-run that check
if re-check passes -> continue remaining checks
if max attempts exceeded or no policy -> fail/pause current stage
```

This keeps stage orchestration in one place and avoids every check re-implementing retry behavior.

**Alternatives considered:** Keeping `ReactionConfig` on `Check` and teaching it to point at task ids was rejected because it preserves the wrong ownership: checks would still decide execution policy. Moving all orchestration into each stage runner was rejected because it would duplicate retry/recheck logic across plan, build, and check stages.

### D2: Replace reactions with stage-local check failure policies

Introduce a small policy type near workflow stage types:

```ts
interface CheckFailurePolicy {
  checkName: string;
  fixTaskId: string;
  maxAttempts: number;
}
```

`BaseStageRunner` should expose a hook such as `getCheckFailurePolicies(): CheckFailurePolicy[]` and a protected `runFixTask(ctx, taskId, failedCheck, attempt)` hook. The base class handles attempt counting and check re-runs; subclasses or a shared fix-task runner execute the actual task identified by `fixTaskId`.

The first built-in mappings should be simple and explicit:

- `health:plan` -> `fix-plan-health` if plan health auto-fix is enabled.
- `health:build` -> `fix-build-health` when build health auto-fix is enabled.
- `health:check` -> `fix-check-health` when check health auto-fix is enabled.
- `ai-review` -> `fix-review-findings` with one attempt.
- Plan artifact checks such as `proposal-complete`, `specs-complete`, `design-complete`, `tasks-valid`, and `self-review-passed` -> `repair-plan-artifacts` where repair is desired.

Policy lookup should be exact by check name. If there is no policy, the stage fails or pauses in its current stage with the failed `CheckResult` persisted.

**Alternatives considered:** A general fallback graph was rejected because the issue explicitly excludes fallback chains. Encoding policy inside `workflow.yaml` first was rejected as too much surface area for the first refactor; defaults can be derived from existing health gate config and made configurable later.

### D3: Model fix behavior as tasks with durable-artifact metadata

Add an internal task execution shape for fix tasks, but keep the persisted public result as `StageTaskResult`:

```ts
interface StageTaskResult {
  taskId: string;
  title: string;
  status: 'completed' | 'failed' | 'skipped';
  artifacts: string[];
  output?: unknown;
  attempts: number;
  duration: number;
}
```

Fix tasks must call the same result and event pathways as regular tasks: `emitStageTaskUpdate(...)` and `appendTaskResult(...)`. Their `artifacts` should usually be `[]` because health and review fixes change code, not durable workflow documents. If a plan repair task updates `proposal.md`, `specs/`, `design.md`, `tasks.json`, or `self-review.md`, those paths may be listed as durable artifacts.

Task output should hold transient execution details such as agent session success, error summaries, command excerpts, changed file summaries, or review fix prompts. Existing `stage_executions` JSON storage can persist this field without schema changes.

**Alternatives considered:** Creating a separate `fix_results` table was rejected because it splits the audit trail users need to read in sequence. Treating fix evidence as artifacts was rejected because it would make transient logs look like files that must be committed or archived.

### D4: Extract health gate fix execution out of `HealthGateCheck`

`HealthGateCheck.run()` should remain responsible for executing the configured command and returning a `CheckResult` with `output.kind = 'health-gate'`, command metadata, duration, exit code, timeout status, summary, and log excerpt. Its `fix()` method should be removed.

The coder-agent prompt construction and `withSession(...)` call currently inside `HealthGateCheck.fix()` should move to a fix task executor. That executor should consume the failed `CheckResult.output` rather than re-running the command only to rediscover the same log. If the output is missing or insufficient, it may re-run the health command as part of the fix task and store that log excerpt in the fix task `output`.

Health fix task ids should be stage-specific: `fix-plan-health`, `fix-build-health`, and `fix-check-health`. The task title should include the failing health gate so UI history is readable.

**Alternatives considered:** Keeping a shared `fixHealthGate(check)` method on the check was rejected because it still gives a check execution capability. Duplicating health fix code in each stage runner was rejected; a shared helper or task executor keeps the implementation deep and localized.

### D5: Extract review fix execution out of `AiReviewCheck`

`AiReviewCheck.run()` should only read `review.md`, parse the verdict, and return parsed evidence in `CheckResult.output`. The auto-fix and re-verify sessions currently in `AiReviewCheck.fix()` should become explicit check-stage tasks.

Use `fix-review-findings` for the coder session that applies review findings. The existing re-review behavior should be modeled deliberately: either as part of the same fix task `output` if it is only transient verification, or as the existing durable `review-self-check`/review task sequence if it writes durable review artifacts. The follow-up `AiReviewCheck.run()` remains the authoritative re-check after the fix task completes.

By default, `fix-review-findings` should have `artifacts: []` because it changes code. It may include transient output such as agent success, session id, and a summary of what it attempted.

**Alternatives considered:** Re-running the full check stage after a failed AI review was rejected because it hides the specific fix action and can regenerate unrelated artifacts. Keeping re-verification hidden inside the check was rejected for the same auditability reason as `fix()`.

### D6: Treat plan artifact repair as a task, not whole-stage retry

The current `retry-task` reaction causes `BaseStageRunner` to re-run `executeTasks()`, which can amplify work and obscure which artifact needed repair. Replace this with a `repair-plan-artifacts` task that receives the failed check name, failed output, change directory, and expected durable artifact paths.

For the first implementation, `repair-plan-artifacts` may reuse the existing artifact retry prompts and checkpoint-aware plan session logic, but it should append a single explicit task result. It should only list durable artifact paths it creates or updates.

**Alternatives considered:** Re-running the original generation task by task id was considered, but several artifact completeness checks validate cross-artifact consistency, not just one source task. A single repair task is simpler and maps to the user-visible recovery action.

### D7: Keep approval checks as checks with pause semantics

`UserApprovalCheck` can remain a check because it verifies approval state rather than mutating code or artifacts. The base runner should keep approval pause handling separate from fix policies: if a failed approval check has no fix policy, it should set approval state and mark the stage execution as `awaiting-approval` as today.

This can be represented by keeping a small base-runner special case for approval checks or by introducing a read-only `CheckResult.output.kind = 'approval-required'` convention. The implementation should not reintroduce generic `ask-user` fallback chains.

**Alternatives considered:** Modeling user approval as a task was rejected because approval is an external gate and does not execute workflow work. Keeping `ask-user` as a general reaction type was rejected because it preserves the old reaction system beyond the one approval use case needed now.

### D8: Preserve API/UI shape, enhance ordering semantics

The API can continue exposing each stage execution as separate `taskResults` and `checkResults`. The UI should render task and check arrays in the recorded order and should not assume that all tasks are known from static stage definitions. Dynamic fix tasks must appear in the task list even when they are not in `PLAN_TASK_DEFS`, `CHECK_TASK_DEFS`, or `INTEGRATE_TASK_DEFS`.

Because task results and check results are persisted in separate arrays, exact interleaving requires either timestamp/order metadata or UI conventions. The minimal first version can show fix tasks in the task section and repeated check results in the checks section, preserving append order inside each section. If exact cross-section sequencing is required, add an optional `sequence` or timestamp field later rather than changing storage in this issue.

**Alternatives considered:** Merging tasks and checks into one persisted timeline was rejected as too large for this refactor. Rendering hidden synthetic tasks in the UI only was rejected because the audit trail must come from persisted execution data, not UI inference.

## Risks / Trade-offs

- [Risk] Existing tests or consumers may expect `check.reaction` or `check.fix()` to exist. → Mitigation: migrate all in-repo checks and tests together, and keep any temporary legacy types private to migration code rather than exported from the main `Check` interface.
- [Risk] Health gate commands may have side effects even when treated as checks. → Mitigation: document checks as verification-only and ensure mohist checks do not intentionally write durable artifacts, modify code, or spawn agents.
- [Risk] Fix tasks that spawn agents can fail without producing useful diagnostics. → Mitigation: always append a failed `StageTaskResult` with transient `output.error`, attempts, and duration before failing the stage.
- [Risk] Repeated check results with the same name can be confusing in the UI. → Mitigation: preserve check result append order and display repeated attempts rather than collapsing by check name.
- [Risk] Deriving new policies from legacy `autoFix` and `maxFixAttempts` could subtly change default behavior. → Mitigation: keep defaults equivalent for health gates and AI review, but stop escalation after max attempts as required by this issue.
- [Risk] `StageTaskResult.output` can grow large if agent logs are stored directly. → Mitigation: store excerpts and summaries in task/check output; full streams stay in existing session stream logs.
- [Risk] Plan repair may rewrite more durable artifacts than necessary. → Mitigation: pass failed check context into the repair prompt and list only changed durable artifacts in the task result.

## Migration Plan

1. Extend `StageTaskResult` with optional `output?: unknown` and update UI/types to tolerate it without requiring artifact output.
2. Introduce `CheckFailurePolicy` and base-runner hooks for policy lookup and fix task execution.
3. Refactor `BaseStageRunner` to remove `handleRetryTask`, `handleAutoFix`, and fallback dispatch from normal check failure handling; keep only approval pause behavior and simple policy-driven fix/recheck handling.
4. Remove `reaction` and `fix?()` from the main `Check` interface, then update all check implementations to expose only `name` and `run(ctx)`.
5. Move health gate fix prompt/session logic into an explicit health fix task executor used by plan/build/check stage runners according to policy.
6. Move AI review auto-fix logic into `fix-review-findings` in the check stage and make `AiReviewCheck` read-only.
7. Replace plan artifact `retry-task` behavior with `repair-plan-artifacts` policy and task execution.
8. Translate legacy health gate `autoFix` and `maxFixAttempts` config into stage-local policies internally; stop using `fallbackReaction` for this issue's execution path.
9. Update `PipelineView` and shared web types so dynamic fix tasks and repeated checks are rendered visibly, and so empty `artifacts` is normal for build/fix tasks.
10. Add regression tests for read-only checks, explicit health/review fix task execution, empty build artifacts, durable artifact preservation, repeated re-check visibility, and max-attempt stage failure without escalation.

Rollback is straightforward before removing legacy code: keep old reaction dispatch behind a temporary compatibility path while new policies are introduced. After the main `Check` interface drops `fix?()`, rollback should be done by reverting this change as a unit because mixed old/new check contracts would reintroduce ambiguity.

## Open Questions

- Should exact cross-section ordering between task results and check results be added now with a `sequence` field, or is ordered display within the separate task/check sections sufficient for the first implementation?
- Should `repair-plan-artifacts` be one generic task for all plan artifact checks, or should it be split later into artifact-specific repair task ids if prompts become too broad?
