## Context

Review stage (`workflow-controller.ts:631-714`) sends one `conn.prompt()` then closes the connection. The Plan stage has 5 rounds (4 artifact + 1 self-review), but Review has only 1 round. This asymmetry means review reports have no quality gate — when the LLM outputs thinking process or an incomplete report, it's stored directly.

The fix follows the exact same pattern as Plan stage's self-review round: reuse the same ACP connection, mutate a `roundState` object that the `onSessionUpdate` closure reads to emit correct `roundType`/`roundIndex` per round.

## Goals / Non-Goals

**Goals:**
- Add a self-check round (round 1) to Review stage so the agent verifies and corrects `review.md`
- Emit correct SSE events (`plan_round_start`, `plan_session_update`) for both rounds
- Validate the final report is non-empty
- Add `buildReviewSelfCheckPrompt` in `artifact-prompt.ts` with a new instruction file `review-self-check.md`

**Non-Goals:**
- More than 2 rounds (no retry loop like Plan stage's artifact rounds)
- Structured report format validation (e.g. parsing markdown headers) — non-empty check is sufficient
- Changes to the WebUI frontend — it already handles arbitrary round types/indices

## Decisions

### D1: Mirror Plan stage's `roundState` pattern for per-round event metadata

Use a mutable `roundState = { type: '', index: 0 }` object shared with the `onSessionUpdate` closure, exactly like Plan stage does at line 91-110. Before each round, set `roundState.type` and `roundState.index`. The closure reads these at event emission time.

**Why:** This is the proven pattern already in production for Plan stage. It avoids creating separate `onSessionUpdate` closures per round or restructuring the event emission system. JS single-threading guarantees the closure always reads the correct round state.

**Alternatives considered:**
- Separate `onSessionUpdate` closures per round — more code, no benefit since the ACP connection is shared
- Refactoring round emission into a shared helper — over-engineering for 2 rounds

### D2: Self-check prompt as a separate instruction file `review-self-check.md`

Create `packages/cli/src/agents/prompts/artifacts/review-self-check.md` with instructions for the agent to read `review.md`, verify its format and completeness, and rewrite if needed. Add `buildReviewSelfCheckPrompt(issue, changeDir)` in `artifact-prompt.ts` following the same structure as `buildSelfReviewPrompt`.

**Why:** Keeps prompt text in markdown files (consistent with all other prompts), not hardcoded in TypeScript.

**Alternatives considered:**
- Inline the self-check prompt in the controller — breaks the prompt-as-file convention
- Reuse the existing `self-review.md` prompt — wrong scope, that one reviews plan artifacts

### D3: Self-check failure returns `success: false` with `review.md` read as diagnostic fallback

If round 1 (self-check) fails, the stage returns `success: false` with `requiresApproval: false`. The `review.md` from round 0 is still read for the error message payload, but the stage does not return `requiresApproval: true` with a potentially bad report.

**Why:** A failed self-check indicates something went wrong with the agent session. Better to surface the failure than silently present an unverified report for approval.

**Alternatives considered:**
- Return `requiresApproval: true` with round 0's `review.md` if self-check fails — hides quality issues, defeats the purpose of self-check

### D4: Non-empty validation only (no structural parsing)

After both rounds, validate that `review.md` content (or `result.text` fallback) is non-empty. No parsing of markdown headers or verdict fields.

**Why:** Structural validation would be fragile and over-engineered. The self-check prompt handles format quality; the non-empty check is a safety net.

## Risks / Trade-offs

- [Self-check round adds ~2-3 min latency to Review stage] → Acceptable trade-off: Review stage currently takes ~3 min total; self-check adds modest overhead for significant quality improvement.
- [Self-check round could fail on an otherwise good report] → Mitigated by the self-check prompt being lightweight verification, not re-generation.
- [Agent may ignore self-check instructions and produce garbage] → Mitigated by the prompt being specific (read file → verify → rewrite). Worst case: non-empty check catches complete failure.

## Migration Plan

No migration needed. This is a pure logic change in the Review stage pipeline. Existing issues in `waiting-review` state will use the new two-round flow when the review stage runs (re-review / re-open). Issues already past review are unaffected.

## Open Questions

None.
