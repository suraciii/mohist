## Why

Review stage (`workflow-controller.ts:631-714`) sends a single `conn.prompt()` and immediately returns the result for approval. When the LLM's first response is poor (e.g. contains reasoning/thinking instead of a structured report), there is no self-check round to correct it. Plan stage already has a self-review round (second prompt for quality check); Review stage lacks the same mechanism, causing unstable report quality (observed in Issue #5, #6).

## What Changes

- Add a self-review round to Review stage: after the first prompt generates the review report, send a second prompt asking the agent to verify the report's format and completeness (mirroring Plan stage's self-review pattern)
- Emit `plan_round_start` with `roundType: 'review-self-check'` and `roundIndex: 1` for the self-review round
- Validate review output before accepting: reject empty or clearly malformed responses
- Update the reviewer prompt to explicitly instruct "output only the final report, no thinking/reasoning process"

## Capabilities

### New Capabilities

- `review-stage-self-check`: Multi-round review pipeline with self-review round for report quality assurance

### Modified Capabilities

- `pipeline-session-events`: Review stage now emits two rounds (round 0: review, round 1: review-self-check) instead of one; `plan_round_start` and `plan_session_update` events for the new round

## Impact

- `packages/cli/src/workflow/workflow-controller.ts` — `runPipelineReviewStage` becomes multi-round with self-check prompt
- `packages/cli/src/agent/prompts.ts` — add `buildReviewSelfCheckPrompt` function
- `openspec/specs/pipeline-session-events/spec.md` — delta for second review round events
- WebUI frontend — already handles arbitrary `roundIndex` values, no code changes needed (only displays what server sends)
