## Why

Review and plan stage self-checks produce PASS/FAIL verdicts, but on FAIL the system currently proceeds to awaiting-user without attempting any fix. This wastes clearly automatable work — the self-check identifies concrete problems (e.g., missing spec compliance, formatting errors) that an agent could fix. When auto-fix is added, it must re-verify via a **full review on a new ACP connection** rather than targeted re-verify, because targeted checks miss regressions introduced by the fix itself.

## What Changes

- Add auto-fix flow after self-check FAIL in both review and plan stages
- After auto-fix, close the existing ACP connection and open a **new** one for unbiased full re-review / re-self-review
- Single attempt only — if re-review still FAIL, go to awaiting-user (no retry loop, no escalation to build)
- Remove `escalateToStage` concept from review stage — FAIL always awaits human
- Remove `no-auto-fix` checkpoint guard and `MAX_AUTO_FIX_ATTEMPTS` — not needed with single-attempt model
- Remove `re-verify.md` targeted prompt template — replaced by full review prompt
- Add verdict (PASS/FAIL) parsing from self-check output in both plan and review stages

## Capabilities

### New Capabilities

- `stage-auto-fix` — unified auto-fix + full re-check pattern: self-check FAIL → auto-fix on same ACP conn → close conn → new conn → full review + self-check → PASS: awaiting user / FAIL: awaiting user

### Modified Capabilities

(none — existing specs don't cover auto-fix behavior)

## Impact

- `packages/cli/src/workflow/workflow-controller.ts` — add verdict parsing + auto-fix loop to `runPipelineReviewStage` and `runPlanStage`
- `packages/cli/src/agents/artifact-prompt.ts` — add auto-fix prompt builder, remove re-verify prompt if it exists
- `packages/cli/src/agents/prompts/artifacts/` — prompt template for auto-fix instructions
- `packages/cli/src/types/workflow-results.ts` — no schema changes needed (verdict is parsed from text, not stored)
