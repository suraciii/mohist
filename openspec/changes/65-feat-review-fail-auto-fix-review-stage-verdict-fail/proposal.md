## Why

Review stage routes to awaiting-user regardless of Verdict PASS or FAIL. When review produces a FAIL with specific Fix Suggestions (file:line level), the system still idles waiting for human reject — a waste of clearly automatable work. Issue #37 is the canonical example: 3 concrete fixes identified but no action taken. Mohist should auto-resolve every problem it can identify.

## What Changes

- Add auto-fix loop inside review stage: when self-check round parses Verdict: FAIL, spawn auto-fix agent round to apply Fix Suggestions, then re-verify
- Cap auto-fix attempts at 2 per review stage entry
- On PASS after auto-fix: update review.md verdict, add issue comment documenting fixes, proceed to awaiting-user
- On exhaustion of attempts: escalate back to build stage with checkpoint `no-auto-fix` marker; second review pass skips auto-fix and goes directly to awaiting-user
- Add `buildAutoFixPrompt` and `buildReVerifyPrompt` to `artifact-prompt.ts`
- Add prompt templates `prompts/auto-fix.md` and `prompts/artifacts/re-verify.md`

## Capabilities

### New Capabilities

- `review-auto-fix`: Auto-fix loop within review stage that applies Fix Suggestions on Verdict FAIL, re-verifies, and escalates on persistent failure

### Modified Capabilities

- `pipeline-model`: CHECK stage now contains internal rounds (R0 review → R1 self-check → R2 auto-fix → R3 re-verify) with branching on Verdict; checkpoint `no-auto-fix` flag controls escalation behavior
- `pipeline-session-events`: New round types `auto-fix` and `re-verify` emitted during review stage auto-fix loop

## Impact

- `packages/cli/src/workflow/workflow-controller.ts` — `runPipelineReviewStage` gains ~80-line auto-fix loop after self-check
- `packages/cli/src/agent/artifact-prompt.ts` — add `buildAutoFixPrompt`, `buildReVerifyPrompt`
- `packages/cli/src/agent/prompts/auto-fix.md` — new prompt template
- `packages/cli/src/agent/prompts/artifacts/re-verify.md` — new prompt template
- No DB schema changes — checkpoint `no-auto-fix` uses existing checkpoint mechanism
- No API or type changes
