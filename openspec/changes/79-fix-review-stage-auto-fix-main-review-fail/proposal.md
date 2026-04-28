## Why

Review stage (`runPipelineReviewStage` at workflow-controller.ts:715) routes to awaiting-user regardless of Result PASS or FAIL — the auto-fix logic from Issue #65 was never merged to main. When review produces FAIL with concrete Fix Suggestions, the system idles waiting for human instead of acting on clearly automatable fixes. Issue #37 is the canonical example: 3 file:line-level fixes identified but no action taken.

## What Changes

- Add result-parsing and auto-fix loop inside `runPipelineReviewStage`: after self-check round, parse `## Result: PASS/FAIL` from review.md; PASS → awaiting-user, FAIL → auto-fix loop
- **BREAKING**: Rename `Verdict` → `Result` in review prompt templates, regex patterns (`VERDICT_RE` → `RESULT_RE`), and function name (`parseVerdict` → `parseResult`). SSE event docs referencing "Verdict" updated accordingly
- Add auto-fix round: spawn agent with Fix Suggestions as prompt, apply fixes
- Add re-verify round: full re-review (not targeted) on new ACP connection to catch regressions
- Cap auto-fix attempts at 2; on exhaustion, escalate to build stage with `no-auto-fix` checkpoint marker
- Second review pass after escalation skips auto-fix and goes directly to awaiting-user
- Split `runPipelineReviewStage` (~150 lines currently, would grow to ~280) into smaller functions
- Update existing prompt templates: `auto-fix.md`, `re-verify.md` (Verdict → Result; re-verify → full re-review)
- `buildAutoFixPrompt` and `buildReVerifyPrompt` already exist in `artifact-prompt.ts` — no changes needed

## Capabilities

### New Capabilities

- `review-auto-fix`: Auto-fix loop within review stage — parses Result from review.md, applies Fix Suggestions on FAIL, re-verifies with full re-review, escalates on persistent failure

### Modified Capabilities

- `pipeline-model`: CHECK stage now contains internal rounds (R0 review → R1 self-check → parse Result → R2 auto-fix → R3 re-verify) with branching on Result; checkpoint `no-auto-fix` flag controls escalation behavior
- `pipeline-session-events`: New round types `auto-fix` and `re-verify` emitted during review stage auto-fix loop

## Impact

- `packages/cli/src/workflow/workflow-controller.ts` — `runPipelineReviewStage` gains auto-fix loop; `parseVerdict` → `parseResult`; function split into ~3 smaller methods
- `packages/cli/src/agent/artifact-prompt.ts` — add `buildAutoFixPrompt`, `buildReVerifyPrompt`
- `packages/cli/src/agent/prompts/auto-fix.md` — update prompt template (Verdict → Result)
- `packages/cli/src/agent/prompts/artifacts/re-verify.md` — update prompt template (Verdict → Result, targeted → full re-review)
- Review prompt templates — `Verdict` → `Result` terminology change
- No DB schema changes — checkpoint `no-auto-fix` uses existing checkpoint mechanism
- Add `escalateToStage?: Stage` field to `StageResult` interface
