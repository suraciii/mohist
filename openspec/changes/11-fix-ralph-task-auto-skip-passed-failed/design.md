## Context

The Ralph executor loop (`runRalphLoop` in `ralph-executor.ts`) has a control-flow bug in its failure handling. When a non-retryable task failure (e.g. timeout) occurs and `onAskUser` is not provided, the auto-skip branch (line 589-592) marks the task as `passes: true` and never increments `failed`. The `failed++` lives in the `else` branch (line 594) which is unreachable when `shouldPause` is true. The final `success` calculation (`failed === 0`) then incorrectly reports `true`.

This is a pure logic fix — no new features, no new API surface.

## Goals / Non-Goals

**Goals:**
- Auto-skipped tasks are recorded as `passes: false` and counted as failures
- `RalphLoopResult` exposes a `skipped` count for observability
- `success` is `false` when any task was auto-skipped or failed
- Downstream consumers (workflow-controller, logs) report accurate counts

**Non-Goals:**
- Adding `onAskUser` to the workflow-controller (separate concern)
- Changing the user-facing skip/retry/abort prompt flow
- Modifying task status in tasks.json beyond the `passes` and `error` fields

## Decisions

### D1: Auto-skip increments both `failed` and new `skipped` counter

The auto-skip branch sets `passes: false`, increments `failed++`, and increments a new `skipped++` counter. `failed` captures "did not pass", `skipped` captures "was auto-skipped without user input".

**Alternatives considered:**
- Only increment `skipped`, keep `failed` for genuinely retried-and-exhausted tasks — rejected because downstream code already checks `result.success` and `result.failed`, adding `skipped` as a separate failure dimension is simpler
- Only increment `failed`, no `skipped` field — simpler but loses observability into why tasks failed

### D2: `success: failed === 0` is sufficient (no `skipped` in the check)

Since auto-skip now increments `failed`, the existing `success: failed === 0` already covers it. Adding `&& skipped === 0` would be redundant. Keeping the simple check avoids changing the contract for existing consumers.

**Update:** Spec says `success: failed === 0 && skipped === 0`. Since `failed` is incremented on auto-skip, both conditions are equivalent when `skipped > 0 → failed > 0`. We'll add `skipped` to `RalphLoopResult` for observability but `success: failed === 0` remains correct.

### D3: Minimal type change — add `skipped` field to `RalphLoopResult`

Add `skipped: number` (optional with default 0 is not needed — always initialize to 0). This is backward-compatible since TypeScript consumers access it optionally.

## Risks / Trade-offs

- **[Existing tasks.json entries with `passes: true` from auto-skip]** → Line 307-314 already resets all-`passes=true` to `false` as a corrupted-state guard. After this fix, auto-skipped tasks will be `passes: false`, so the guard won't interfere. No migration needed.
- **[workflow-controller accesses `result.failed` but not `result.skipped`]** → No breakage; `skipped` is additive. Controller will see correct `failed > 0` and `success: false`.

## Migration Plan

Single PR, no migration. The fix is self-contained in `ralph-executor.ts` and the `RalphLoopResult` type. No database schema changes, no API contract changes visible to external consumers.

## Open Questions

_None_.
