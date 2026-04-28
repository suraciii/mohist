## Context

The review stage (`runPipelineReviewStage` at workflow-controller.ts:715) currently runs R0 (review) → R1 (self-check) → returns `requiresApproval: true` regardless of review result. The `parseVerdict` function exists at line 895 but is dead code — never called. Prompt templates for auto-fix (`auto-fix.md`) and re-verify (`re-verify.md`) already exist in the codebase from prior work, and `buildAutoFixPrompt` / `buildReVerifyPrompt` are already implemented in `artifact-prompt.ts` (lines 286-331). The `commentRepo` is already wired into `WorkflowController` (line 67). What's missing is the control flow that invokes these pieces after parsing the review Result.

### Existing infrastructure we can reuse

- `PipelineCheckpointRepo` — `upsert(issueNumber, stage, completedSteps, nextStep)` / `get(issueNumber, stage)` for checkpoint persistence
- `CommentRepo.create({ issueId, body })` for adding issue comments
- `createAcpConnection(acpOptions)` for spawning new ACP connections per round
- `buildAutoFixPrompt` / `buildReVerifyPrompt` — already implemented in `artifact-prompt.ts`
- `readReportFile(changeDir, filename)` — already exists at line 869
- `eventBus.emit('plan_round_start', ...)` / `eventBus.emit('plan_session_update', ...)` — already used for R0/R1 events
- The `roundState` closure pattern — `roundState.type` / `roundState.index` mutated per round, captured by `onSessionUpdate` callback

### Current StageResult interface

```typescript
interface StageResult {
  success: boolean;
  requiresApproval: boolean;
  output: unknown;
  message?: string;
}
```

No `escalateToStage` field exists. The proposal mentions it was added by Issue #65 but that was never merged.

## Goals / Non-Goals

**Goals:**
- Parse `## Result: PASS|FAIL` from review.md after self-check and branch accordingly
- Implement auto-fix loop (max 2 attempts): auto-fix round → re-verify round → check Result
- On auto-fix PASS: add issue comment with Fix Suggestions, proceed to awaiting-user
- On loop exhaustion: set `no-auto-fix` checkpoint, escalate to build stage
- On second review pass with `no-auto-fix` checkpoint: skip auto-fix, go directly to awaiting-user
- Rename Verdict → Result across prompts, regex, functions (with backward compat for legacy `## Verdict:`)
- Decompose `runPipelineReviewStage` into focused methods

**Non-Goals:**
- Layer 3 dimension-level parsing (Correctness, Test Coverage, etc.) — future enhancement
- Modifying the build stage to handle escalation context (separate change)
- Changing DB schema or API types

## Decisions

### D1: Re-verify does full re-review on a NEW ACP connection

Each auto-fix + re-verify cycle uses a fresh `createAcpConnection()` call, not reusing the previous connection. Auto-fix round and re-verify round each get their own connection. This ensures clean state for the re-verify agent and prevents stale context from the auto-fix session leaking into the review.

**Alternatives considered:**
- Reuse same ACP connection across all rounds (R0→R1→R2→R3) — simpler but risks context pollution between auto-fix and re-verify; also the review/self-check connection is already closed at line 821
- Reuse auto-fix connection for re-verify — the auto-fix agent has fix-specific context that would bias the re-verify

### D2: Checkpoint key format: `stage='review'`, `completedSteps=['no-auto-fix']`

Use the existing `PipelineCheckpointRepo.upsert(issueNumber, 'review', ['no-auto-fix'], null)`. Checking for the checkpoint uses `checkpointRepo.get(issueNumber, 'review')?.completedSteps.includes('no-auto-fix')`. This follows the same pattern as plan stage checkpoints (which store round types as completedSteps).

**Alternatives considered:**
- Custom key like `checkpointRepo.get(issueNumber, 'review-no-auto-fix')` — doesn't match existing pattern, would require a compound stage name
- Store in Issue metadata — requires schema changes, checkpoint repo is designed exactly for this

### D3: Auto-fix round failure counts as one attempt, does not break the loop

When the auto-fix ACP round fails (error, timeout), we log it and increment the attempt counter, then continue to re-verify (or exit if max reached). The re-verify on a new connection will read whatever state the codebase is in and produce a fresh review.md. This is safer than returning an error immediately because the auto-fix agent may have made partial progress.

**Alternatives considered:**
- Immediately return error on auto-fix failure — Issue #65 had this bug; partial fixes would be lost
- Retry the same auto-fix attempt — risks infinite loop on persistent failures

### D4: Issue comment includes original Fix Suggestions, not agent output

When auto-fix succeeds, the comment body includes the original Fix Suggestions section from review.md (not the auto-fix agent's stdout). This gives the user clear visibility into what was identified and presumably fixed.

**Alternatives considered:**
- Include auto-fix agent stdout — Issue #65 had this bug; agent output is verbose and often truncated
- No comment — user has no visibility into what auto-fix changed

### D5: Add `escalateToStage` to StageResult, handle in `run()` switch

Add `escalateToStage?: Stage` to `StageResult`. When `runPipelineReviewStage` returns `{ success: false, escalateToStage: Stage.Build }`, the `run()` method's `Stage.Review` case detects this, sets `no-auto-fix` checkpoint, and updates issue stage back to Build (continuing the while loop).

**Alternatives considered:**
- Return `{ success: true, requiresApproval: true }` on exhaustion — misleading, the review didn't pass
- Throw an error — disrupts the pipeline flow, hard to distinguish from real errors

### D6: Verdict → Result migration with backward compat

Rename `VERDICT_RE` → `RESULT_RE` and `parseVerdict` → `parseResult`. The new regex matches `## Result: PASS|FAIL`. Add a secondary fallback regex for legacy `## Verdict:` with a deprecation log. Update all three prompt templates (review.md, review-self-check.md, re-verify.md) to use `## Result:` in their output format instructions.

### D7: Function decomposition of runPipelineReviewStage

Split into:
- `runPipelineReviewStage` — orchestrator (~60 lines): R0/R1, parse Result, delegate to auto-fix or return
- `runAutoFixLoop` — loop controller (~80 lines): iterate attempts, call auto-fix + re-verify, handle outcomes
- `runReviewRound` — single round helper (~30 lines): create connection, emit round start, run prompt, close connection
- `extractFixSuggestions` — pure function (~10 lines): extract Fix Suggestions section from review.md

### D8: Re-verify prompt update to full re-review

The current `re-verify.md` prompt says "Verify ONLY the specific Fix Suggestions — do not perform a full re-review". This must change to instruct full re-review instead, since auto-fix can introduce new issues. The prompt file already exists — just needs content update.

## Risks / Trade-offs

- **[Auto-fix agent makes things worse]** → Re-verify is full re-review on new connection, catching regressions; max 2 attempts limits damage
- **[Review.md uses legacy `## Verdict:` header from existing review agents]** → Backward compat regex fallback with deprecation log; prompt templates updated to produce `## Result:`
- **[Escalation to build stage with no-auto-fix means user never gets auto-fix for that issue]** → Intentional: after 2 failed attempts, human intervention is appropriate; checkpoint is cleared when pipeline restarts
- **[Auto-fix + re-verify adds latency]** → Each attempt is 2 ACP sessions; max 4 extra sessions per review stage. Acceptable trade-off for automated fix capability
- **[Re-verify on new connection loses review context]** → The re-verify prompt includes the previous review report as input; the agent has full context from the file, not just from session memory

## Migration Plan

1. Add `escalateToStage?: Stage` to `StageResult` interface
2. Update `run()` Stage.Review case to check `escalateToStage` and handle escalation
3. Update `parseVerdict` → `parseResult` with Result regex + Verdict fallback
4. Update prompt templates (review.md, review-self-check.md, re-verify.md): `Verdict` → `Result`
5. Decompose `runPipelineReviewStage` into helper methods
6. Add auto-fix loop logic with checkpoint integration
7. No DB schema changes; no API breaking changes
8. Rollback: revert the function decomposition; the checkpoint `no-auto-fix` is harmless if auto-fix code is removed

## Open Questions

None — all decisions resolved per Issue description and specs.
