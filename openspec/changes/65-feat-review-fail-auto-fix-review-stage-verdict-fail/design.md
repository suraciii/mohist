## Context

Review stage (`runPipelineReviewStage` at `workflow-controller.ts:694`) currently runs two rounds — R0 (review) and R1 (self-check) — then unconditionally returns `requiresApproval: true` with the review report, regardless of Verdict. The `StageResult` interface has no escalation signal.

The `run()` loop at line 310 is a `while (stage !== Done)` switch that dispatches to stage-specific methods. Build stage already supports checkpoint-based resume (`PipelineCheckpointRepo`). The escalation path (review → build) requires extending `StageResult` and the `run()` loop to handle stage regression.

Existing prompt infrastructure: `artifact-prompt.ts` loads `.md` files from `prompts/artifacts/` and `prompts/`. The pattern is: load file → inject issue/changeDir context → return prompt string. `review.md` output format has a well-defined `## Verdict: PASS/FAIL` and `## Fix Suggestions` section.

## Goals / Non-Goals

**Goals:**
- Auto-fix loop inside review stage (max 2 attempts) when Verdict: FAIL
- Re-verify targeted issues after auto-fix
- Escalate to build stage when auto-fix exhausted
- Skip auto-fix on second review pass (checkpoint guard)
- Record fix history as issue comment
- Emit SSE events for new round types (`auto-fix`, `re-verify`)

**Non-Goals:**
- Classifying FAIL severity (FAIL = auto-fix, no tiers)
- Changing DB schema or API surface
- Full re-review on re-verify (targeted only)
- Modifying plan stage or build stage internals
- Auto-approving after successful auto-fix (still awaits human)

## Decisions

### D1: Extend StageResult with `escalateToStage` field

Add optional `escalateToStage?: Stage` to `StageResult`. When set, the `run()` loop transitions the issue to that stage instead of the default progression. This avoids a separate return type or exception-based flow.

**Alternatives considered:**
- Return a union type `ReviewStageResult = NormalResult | EscalateResult` — more type-safe but requires discriminated union handling at every call site
- Throw a custom `EscalateError` — couples control flow to exceptions, poor practice for expected paths
- Handle escalation inside `runPipelineReviewStage` itself by calling `issueRepo.updateStage` directly — breaks the single-responsibility of the stage method and duplicates stage transition logic

### D2: Verdict parsing via regex on review.md

Parse `## Verdict: PASS` or `## Verdict: FAIL` from `review.md` using a simple regex `/\#\s*Verdict:\s*(PASS|FAIL)/i`. The review.md format is controlled by our prompt, so this is reliable.

**Alternatives considered:**
- LLM-based verdict extraction — overkill, slow, non-deterministic
- Structured JSON output from review agent — would require changing review prompt format, breaking existing behavior

### D3: Auto-fix and re-verify share the same ACP connection

Keep the connection open across R0→R1→R2→R3→... rounds (same as plan stage pattern). The existing `conn.prompt()` multi-round pattern already supports this. Close only when the method returns.

**Alternatives considered:**
- New ACP connection per round — adds latency, loses conversation context between rounds
- Separate connection for auto-fix only — unnecessary complexity, context is useful

### D4: Comment via CommentRepo injected into WorkflowController

Add optional `commentRepo?: CommentRepo` to `WorkflowControllerOptions`. Used only for auto-fix history comments. If not provided, comment is skipped (graceful degradation).

**Alternatives considered:**
- Use `issueService.addComment()` — would require injecting the full service, tight coupling
- Write comment file to changeDir — not visible to users, defeats the purpose

### D5: Escalation sets checkpoint then returns escalation signal

`runPipelineReviewStage` writes `no-auto-fix` checkpoint, then returns `StageResult { success: true, escalateToStage: Stage.Build }`. The `run()` loop handles the stage transition. This keeps stage transition logic centralized.

**Alternatives considered:**
- Review stage directly calls `issueRepo.updateStage(Stage.Build)` — breaks the "stage methods don't do transitions" convention

## Risks / Trade-offs

**[Auto-fix introduces bad code]** → Auto-fix agent has the same tools as build stage (read, write, bash). Build verification in re-verify round catches compilation errors. Human still approves before done.

**[Regex verdict parsing is brittle]** → The review.md format is dictated by our prompt (`review.md` template). Self-check round already validates the format. If parsing fails, fall through to awaiting-user (safe default).

**[Auto-fix loop adds latency]** → Max 2 attempts × 2 rounds = 4 extra ACP prompts. Each is a targeted prompt (not full review), so faster than the initial review. Acceptable trade-off vs. human wait time.

**[Escalation could re-enter infinite review-build loop]** → Checkpoint `no-auto-fix` prevents this: first escalation sets checkpoint, second review pass skips auto-fix entirely. Pipeline completion clears all checkpoints.

## Migration Plan

1. Add `escalateToStage` to `StageResult` interface (backward compatible — optional field)
2. Add `commentRepo` to `WorkflowControllerOptions` (optional — existing callers unaffected)
3. Add prompt files (`auto-fix.md`, `re-verify.md`) and builder functions
4. Add auto-fix loop to `runPipelineReviewStage`
5. Update `run()` loop to handle `escalateToStage` in Review case
6. Wire `commentRepo` in `agent-runner-service.ts` where `WorkflowController` is constructed
7. No DB migration — uses existing `pipeline_checkpoint` table

Rollback: Remove auto-fix loop and `escalateToStage` handling. Review stage returns to R0→R1→awaiting. No data migration to undo.

## Open Questions

None. All design decisions are resolved (D1-D8 from issue description plus D1-D5 above).
