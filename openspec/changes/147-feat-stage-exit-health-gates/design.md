## Context

Mohist already has a useful execution shape for stage boundaries: `BaseStageRunner` runs stage work, then executes ordered `Check` instances, persists `checkResults` in `stage_executions`, and dispatches failures through `ReactionConfig`. The current health behavior is fragmented inside specialized checks and merge code: plan checks artifacts and approval only, build runs `AllTasksCompleteCheck` plus `CodeCompilesCheck`, check runs `BuildTestCheck` before AI review and approval, and `MergeQueue` runs a hard-coded build verification only after a rebase while the direct merge API can mark an issue done immediately.

The design should pull health verification into one reusable check implementation and one shared post-merge finalization path, without adding a separate lifecycle table or making stage runners responsible for shell-command details.

## Goals / Non-Goals

**Goals:**

- Provide an explicit enabled/disabled health gate policy for plan, build, check, and post-merge completion.
- Run health gates as normal `Check` items before `UserApprovalCheck` at approval stages.
- Persist gate results in existing stage execution check results with command, duration, summary, and log excerpt.
- Preserve compatibility with existing `checks.buildTest` workflow config by using it as the default check-stage full verification gate when no new per-stage gate config exists.
- Make direct merge and merge queue completion use the same final health verification rule before `stage=done` and `status=completed`.
- Keep the interface small: stage runners ask for checks; check implementations hide command execution, output shaping, and optional auto-fix behavior.

**Non-Goals:**

- Do not introduce a new health-gate persistence table.
- Do not require every stage to run the full test suite by default.
- Do not rely on agent prompt wording as a health guarantee.
- Do not redesign the whole workflow engine, issue status model, or merge queue state machine.

## Decisions

### D1: Model Health Gates As Parameterized Checks

Introduce a reusable `HealthGateCheck` that implements the existing `Check` interface. It accepts a resolved gate policy containing `stage`, `name`, `enabled`, `command`, `timeout`, `autoFix`, `maxFixAttempts`, and fallback reaction. If disabled, the check returns `pass` with `output.enabled=false` so the policy is visible without blocking progression.

The check runs the configured shell command in the issue worktree, captures stdout/stderr, measures duration, extracts a short error summary, and returns a `CheckResult` shaped for UI/API consumption:

```ts
{
  name: 'health:build',
  status: 'pass' | 'fail' | 'error',
  message: 'Build health gate passed',
  output: {
    kind: 'health-gate',
    stage: 'build',
    command: 'npm run build',
    timeout: 300000,
    duration: 12345,
    enabled: true,
    exitCode: 1,
    timedOut: false,
    summary: 'Build failed (exit code 1) ...',
    logExcerpt: '...',
  },
}
```

`BuildTestCheck` and `CodeCompilesCheck` should become thin compatibility wrappers or be replaced at call sites by `HealthGateCheck` using the corresponding resolved policy. The reusable check owns the shell execution and result formatting so command failure details do not diverge between plan/build/check/post-merge.

**Alternatives considered:** Keep separate `BuildTestCheck`, `CodeCompilesCheck`, and merge verification implementations. This is smaller initially but preserves the current scattered guarantees and makes output compatibility harder. A separate `HealthGateService` plus custom runner logic was also considered, but using `Check` directly keeps the abstraction deep and lets existing `BaseStageRunner` handle sequencing, persistence, and reactions.

### D2: Resolve Gate Policy Centrally From Workflow Config

Extend workflow config parsing with a central health gate config resolver. The preferred shape is a top-level `healthGates` map keyed by stage boundary:

```yaml
healthGates:
  plan:
    enabled: true
    command: npm run typecheck
    timeout: 300000
    autoFix: false
    fallbackReaction:
      type: ask-user
  build:
    enabled: true
    command: npm run build
    timeout: 300000
    autoFix: true
    maxFixAttempts: 2
    fallbackReaction:
      type: escalate
      escalateTarget: plan
  check:
    enabled: true
    command: npm run build && npm test
    timeout: 300000
    autoFix: true
    maxFixAttempts: 2
    fallbackReaction:
      type: escalate
      escalateTarget: build
  postMerge:
    enabled: true
    command: npm run build && npm test
    timeout: 300000
    autoFix: false
    fallbackReaction:
      type: escalate
      escalateTarget: check
```

Defaults should be explicit in code and match the issue intent: plan uses `npm run typecheck`, build uses `npm run build`, check uses `npm run build && npm test`, and post-merge uses the same command as check unless configured otherwise. If `healthGates.check` is absent and existing `checks.buildTest` is present, map `checks.buildTest.command`, `timeout`, `autoFix`, and `maxFixAttempts` into the check-stage gate. Existing projects without new config continue to see the current check-stage full verification behavior.

The resolver should expose a small API such as `loadHealthGatePolicies(workflow): Record<HealthGateStage, HealthGatePolicy>`. Invalid or missing per-field values fall back field-by-field to defaults rather than disabling the gate accidentally.

**Alternatives considered:** Put health gate config on each `stages[]` entry. That keeps config close to stage prompts but makes post-merge awkward because it is not a normal runner stage, and it duplicates defaults across stage declarations. Reusing only `checks.buildTest` was also considered, but it cannot express plan/build/post-merge policies independently.

### D3: Keep Approval As A Check Ordered After Health

Stage runners should construct ordered check lists so health runs before approval:

- Plan: artifact checks, self-review check, `health:plan`, `UserApprovalCheck(Stage.Plan)`.
- Build: `AllTasksCompleteCheck`, `health:build`.
- Check: `health:check`, `AiReviewCheck`, `UserApprovalCheck(Stage.Check)`.

This preserves existing `BaseStageRunner` behavior: the first failing check stops the sequence, persists partial results, and dispatches a configured reaction. Approval is requested only if every earlier check passes. The `stage_executions.status` remains `awaiting-approval` only for the user-approval check, and `failed` for health-gate failures.

**Alternatives considered:** Add a pre-approval hook to `BaseStageRunner`. That would create a second check path and make ordering harder to inspect. Encoding health in `executeTasks` was also rejected because it would hide failures from `checkResults` and bypass reusable reactions.

### D4: Use Existing ReactionConfig For Failure Handling

Each health gate owns a `ReactionConfig` derived from policy. Auto-fix gates use the existing `auto-fix` reaction and `maxAttempts`; non-auto-fix gates use the configured fallback directly. The auto-fix implementation should be generic: rerun the command to capture fresh output, spawn a coder session with a prompt naming the gate stage and command, then let `BaseStageRunner` rerun the failed check.

Default fallback reactions should be conservative:

- Plan gate: ask user or fail without auto-fix by default, because plan artifacts may not have changed code and a missing typecheck script may require project input.
- Build gate: auto-fix then escalate to plan when exhausted.
- Check gate: auto-fix then escalate to build when exhausted.
- Post-merge gate: no auto-fix by default in the target branch; mark merge completion blocked/failed and surface manual recovery unless explicitly configured.

**Alternatives considered:** Add new failure states such as `health_failed` or `blocked_by_health_gate`. Existing merge states and stage execution statuses are enough for the first implementation, and new states would create UI/API migration work without improving the guarantee.

### D5: Route All Completion Through Shared Post-Merge Verification

Create a small completion helper, for example `IssueCompletionService` or `PostMergeFinalizer`, that is responsible for:

- Running `health:postMerge` in the target project path after `mergeBack` succeeds.
- Persisting or emitting the gate result using existing stage execution/check-result mechanisms where possible; if no active stage execution exists, append a check result to the latest check-stage execution or create a final check-stage execution record scoped to the issue.
- Setting `mergeState=Merged`, clearing approval, updating `stage=done`, and setting `status=completed` only after final gate success.
- Returning a structured failure result that direct API handlers and merge queue callbacks can expose without marking the issue done.

`MergeQueue.processItem` should call this helper after `mergeBack` succeeds instead of setting `MergeState.Merged` and firing completion callbacks directly. The direct `POST /api/issues/:number/merge` endpoint should call the same helper instead of updating issue stage/status inline. `AgentRunnerService` resume logic that advances `Stage.Check` + `MergeState.Merged` issues to Done should either call the helper or only trust a stored finalization marker produced by the helper, so recovery cannot reintroduce a bypass.

Post-merge command working directory should be the project root after merge, not the issue worktree, because the guarantee is about the branch that will be considered complete. If a configured project command must run elsewhere, users can encode `cd packages/cli && npm test` in the shell command.

**Alternatives considered:** Keep post-merge verification inside `MergeQueue` and add similar code to direct merge. That duplicates the highest-risk part of the feature. Making Done a real runner stage was also considered but would require larger workflow-engine changes because the engine currently stops before running `Stage.Done`.

### D6: Preserve Existing Storage And Surface Semantics

Do not create a health-gate table. Health gate results are check results, so they should stay in `stage_executions.check_results` and be distinguishable by `output.kind = 'health-gate'` and check names like `health:plan`. Existing log storage can continue to store command output excerpts only, with full command output truncated to a bounded size to avoid database and API bloat.

UI/API/CLI should treat health-gate check results as first-class stage checks. The required distinction is simple: stage work can be complete while the stage execution is still failed because `health:*` failed; approval panels should render only after `UserApprovalCheck` has been reached.

**Alternatives considered:** Add specialized health-gate fields to issue rows. That would make querying the latest health state easy but would duplicate historical check execution data and require schema migration for information already present in stage execution records.

## Risks / Trade-offs

- [Default commands may not exist in every project] → Fail visibly with the command and summary, and allow disabling or overriding each gate in `workflow.yaml`.
- [Plan gate can slow down planning even when only OpenSpec artifacts changed] → Keep the plan command configurable and lightweight; the first implementation runs unconditionally for a clear guarantee.
- [Post-merge full verification can be expensive] → Default to the check gate for safety, but make `healthGates.postMerge.command` and `enabled` explicit project policy.
- [Post-merge failure happens after merge already changed the target branch] → Do not mark the issue done; set/keep a failed merge-visible state and surface remediation. This cannot undo the merge safely without introducing destructive behavior.
- [Auto-fix after post-merge could mutate the target branch unexpectedly] → Default post-merge auto-fix off. If enabled later, require a deliberate policy and clear commit behavior.
- [Persisting post-merge results without an active runner execution is awkward] → Prefer appending to the latest check-stage execution or creating a final check-stage execution record over adding a new table.
- [Changing check names can break UI tests or consumers] → Use stable names (`health:plan`, `health:build`, `health:check`, `health:postMerge`) and keep compatibility wrappers for old `build-test` output during transition if needed.

## Migration Plan

1. Add `HealthGatePolicy` types, defaults, and `loadHealthGatePolicies` in workflow config loading.
2. Implement `HealthGateCheck` with command execution, output truncation, summary extraction, and optional generic auto-fix.
3. Replace plan/build/check runner check lists with resolved health gate checks in the correct order, preserving artifact/task/AI-review/user-approval checks.
4. Keep `checks.buildTest` compatibility by mapping it into `healthGates.check` when the new check-stage gate is absent; update existing `BuildTestCheck`/`CodeCompilesCheck` call sites or wrappers accordingly.
5. Add a shared post-merge finalizer and route both merge queue and direct merge API through it before writing `stage=done` or `status=completed`.
6. Update API/UI/CLI rendering as needed to display health-gate check results and avoid approval UI before health gates pass.
7. Add tests for policy defaults, `checks.buildTest` fallback, approval ordering, health failure result shape, build/check stage blocking, direct merge bypass prevention, and post-merge finalization.

Rollback is straightforward for pre-merge gates by disabling individual `healthGates.*.enabled` policies. If post-merge verification causes operational issues, projects can temporarily set `healthGates.postMerge.enabled=false`; the code path should still document and return that completion used a weaker policy.

## Open Questions

- Should the built-in plan default use `npm run typecheck` unconditionally, or should Mohist detect script existence and return a skipped/disabled result when missing?
- Should post-merge default to full check (`npm run build && npm test`) for maximum trust, or build-only for latency? This design chooses full check by default but keeps it configurable.
- Should repeated health gate failures eventually mark the issue blocked, or is escalation to plan/build/check sufficient for the first implementation?
- Should post-merge verification failures use `MergeState.BuildFailed`, a new merge state, or blocked status copy only? The first implementation can reuse `BuildFailed`, but UI copy should clarify that the merge succeeded and final verification failed.
