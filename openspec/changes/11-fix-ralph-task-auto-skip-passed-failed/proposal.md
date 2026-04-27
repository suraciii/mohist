## Why

Ralph executor silently marks timed-out tasks as `passes: true` and never increments the `failed` counter, causing the loop to report `success: true` with `failed: 0` even when tasks have definitively failed. This makes it impossible to detect build failures from the Ralph result, breaking the entire workflow reliability guarantee.

## What Changes

- Fix auto-skip branch (`ralph-executor.ts:589-592`) to set `passes: false` instead of `passes: true` for auto-skipped tasks
- Increment `failed++` in the auto-skip branch so the counter reflects reality
- Add `skipped` counter to the Ralph loop result and track auto-skipped tasks separately
- Change `success` calculation to `success: failed === 0 && skipped === 0` so auto-skipped tasks prevent false success
- Update `RalphLoopResult` type to include `skipped` field

## Capabilities

### New Capabilities

_None_

### Modified Capabilities

- `ralph-task-execution` — Task failure handling and loop completion semantics change: auto-skipped tasks now count as failures (not passes), and loop `success` reflects skipped tasks.

## Impact

- `packages/cli/src/openspec/ralph-executor.ts` — Core fix: auto-skip branch logic, failed/skipped counters, success calculation
- `packages/cli/src/openspec/types.ts` — `RalphLoopResult` type addition (if `skipped` field is added)
- `packages/cli/src/workflow/workflow-controller.ts` — Consumer of `RalphLoopResult`, may need to handle `skipped` field
- Consumers of `RalphLoopResult` (API responses, log output) — Will now correctly report failures
