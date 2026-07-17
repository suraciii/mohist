# Review Report

## Result: PASS

This review evaluates the **post-build candidate snapshot** (merge-base `3a8b96dd`..HEAD), after the T-002 cleanup-loop resolution pass landed (commit `9919097e9`) on top of the T-001 registry data model (`10608228f`). Both tasks are now implemented; the previous review's blocking items (missing resolution pass, dead-code `markStuck`, looping test still present) are resolved.

## Acceptance-criteria trace (built candidate)

- **AC#1 — stops warning every tick after the first observation: MET.** The resolution pass (`cleanup-loop.ts:63-70`) emits exactly one `refused to remove` warning and transitions the entry to `stuck` via `markStuck`. On the next tick the entry is excluded by the `phase === "eligible"` filter (`cleanup-loop.ts:51`), so the resolution pass never sees it again. Covered by `cleanup-loop-guards.spec.ts:76` ("warns once ... does not re-warn or re-evaluate it on the next tick") which asserts `warningSpy` is not called on tick 2.
- **AC#2 — resolved deterministically, no longer re-evaluated as `eligible`: MET.** `markStuck` (`workspace-registry.ts:173-180`) transitions `eligible → stuck` and persists atomically. Every eligible-listing site (`cleanup-loop.ts:51, 82, 95`) filters `phase === "eligible"`, so a stuck entry is never re-selected. Covered by the three guard-refusal tests (`cleanup-loop-guards.spec.ts:19/38/57`) asserting `phase: "stuck"` and `deletedPaths` does not contain the path.
- **AC#3 — disabled policy does not keep doing work every tick: MET.** The resolution pass runs (`cleanup-loop.ts:63`) **before** the `retentionDisabled && budgetDisabled` early-return (`cleanup-loop.ts:76`), so resolution is policy-independent. Covered by `cleanup-loop-guards.spec.ts:109` ("resolves a stuck entry even when both retention and budget are disabled") + its tick-2 no-work assertion.
- **AC#4 — path-guard safety preserved: MET.** The three guards are unchanged in `evaluateGuards` (`cleanup-loop.ts:150-167`): out-of-root, missing/unreadable marker, marker mismatch each return `{ ok: false }` and the directory is never deleted on refusal (`safeRemove` returns `false` at `cleanup-loop.ts:173` before any `deleteDirectory`). Covered by the direct `safeRemove` tests (`cleanup-loop-guards.spec.ts:228/244/259`).
- **AC#5 — does not survive restart into the same stuck state: MET.** `markStuck` persists via the atomic write-through (`workspace-registry.ts:179`), and `loadFromDisk` accepts `"stuck"` (`workspace-registry.ts:252`). Covered by `cleanup-loop-guards.spec.ts:142` ("survives a registry reload ... does not reappear as eligible after restart") and `workspace-registry.spec.ts` `Load_StuckEntry_RoundTripsThroughReload`.

## Verification performed

- `npm run typecheck -w packages/runner` — clean.
- `npm run check:test-boundaries -w packages/runner` — pass (97 active Vitest files, no size/title violations).
- `npm test -w packages/runner` — 95 files, **1088 tests pass**.
- `rg "markStuck" packages/runner/src` — production caller present at `cleanup-loop.ts:68` (resolution pass); no longer dead code.

## Correctness notes (no defect found)

- **No stale iteration after resolution.** The retention pass re-lists eligible after the resolution pass (`cleanup-loop.ts:82`), and the budget pass re-lists again (`cleanup-loop.ts:95`). Entries marked `stuck` are excluded from both eviction passes, so they are neither double-processed nor re-warned within the same tick. (The retention pass previously iterated the pre-resolution snapshot; this was corrected during the build.)
- **Counter semantics stay consistent.** Guard refusals caught by the resolution pass increment `stuckResolved`; the `guardAborted` counter now only fires in the documented mid-tick race where `safeRemove`'s defensive re-check refuses (design D3 backstop). No double-counting: an entry is counted once, in exactly one counter.
- **`markStuck` precondition aligns with design D1.** Guard is `phase !== "eligible"` (`workspace-registry.ts:177`), so only `eligible → stuck` transitions; an `active` entry is a no-op (covered by `MarkStuck_OnActiveEntry_IsNoOpAndDoesNotPersist`). `markEligible`'s `phase !== "active"` guard (D4) prevents a redelivered terminal event from reviving a stuck entry.
- **Abort handling.** Mid-pass abort breaks the resolution loop and returns the partial result (`cleanup-loop.ts:64, 71`); already-stuck entries are persisted, remaining eligible entries stay eligible for the next tick. No state corruption.
- **Data safety.** `markStuck` mutates only the registry phase and persists; it never deletes, moves, or touches the workspace directory. The delete guards remain mandatory before any `deleteDirectory`.

## Repaired Items

None. The candidate required no in-review repair: typecheck, boundaries, and the full suite are green, and the implementation matches the approved design (D1–D6) and the spec scenarios.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/runner/src/runtime/cleanup-loop.ts:88-90` (retention) and `:123-125` (budget) — defensive race backstop
  Evidence: The design's accepted mid-tick race (a marker is deleted between the resolution pass and the eviction pass → `safeRemove`'s guard refuses → `guardAborted++`, entry stays `eligible`, resolved on the next tick's resolution pass) is implemented and documented but has no direct test. The direct `safeRemove` tests cover the guard-refusal path in isolation; the cross-tick tests cover the normal resolution path. The race itself ("guards passed in resolution, then failed in eviction within the same tick") is genuinely rare and the accepted outcome is a single extra warning, so this is a coverage nicety, not a defect.
  SuggestedAction: Optionally add a test that flips `markerRunIds` between the resolution pass and the eviction pass (e.g. via a `CleanupRunner` stub hook) to pin the race behavior and the `guardAborted` increment.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/runner/src/runtime/host.ts:357-359` (summary log surface, D5)
  Evidence: The new `stuck=${result.stuckResolved}` log field and the `result.stuckResolved > 0` log condition are not directly asserted. The existing `runner-host-cleanup-config.spec.ts` deliberately stubs `runOnce` and asserts the fetchConfig channel rather than the formatted log line (consistent with that spec's stated scope), and the `stuckResolved` counter itself is fully tested via the loop specs. Asserting `console.log` output here would be brittle and low-value.
  SuggestedAction: None required. If operator-facing log observability ever becomes a concern, add a focused log-spy assertion on the summary line.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/runner/src/runtime/workspace-registry.ts` (stuck-phase lifecycle)
  Evidence: Resolved `stuck` entries remain in the registry indefinitely (no time-based GC). This is an explicit, documented design trade-off (design Risks: bounded by the rare rate of marker loss; no new disk growth since the directory is not deleted; an operator can clean a stuck entry via the manual `RemoveWorkspace` handler, which is phase-agnostic — `workspace-removal-handler.ts:81-84`). Not a defect in this change.
  SuggestedAction: If accumulation ever matters operationally, add a separate "GC stuck entries older than N" pass or surface a stuck-entry list to operators (design Open Questions).
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: `packages/runner/src/runtime/cleanup-loop.ts:176-183` (`safeRemove` delete-failure catch)
  Evidence: A *persistent* `deleteDirectory` failure (chronic EBUSY/EPERM) still leaves the entry `eligible` and retries every tick — the same loop shape as the guard-refusal bug, but a different root cause (transient I/O vs deterministic guard refusal). Design Non-Goals explicitly defer this ("needs a retry budget — out of scope"); it is correctly not addressed by this change.
  SuggestedAction: Future issue — add an attempt counter / retry budget on the entry and resolve to `stuck` (or a dedicated state) after N failed deletes.
  Status: out-of-scope

- [ID: item-5]
  Severity: info
  Scope: `packages/runner/src/runtime/cleanup-loop.ts:48-49` (null/undefined policy early-return)
  Evidence: When `fetchConfig` returns `null`/`undefined` (cleanup subsystem entirely unconfigured), `runOnce` returns before listing eligible entries, so the resolution pass does not run and an existing stuck-eligible entry is not resolved on those ticks. This matches the approved design (D2 places the resolution pass after the `!policy` check) and trivially satisfies AC#3 for the null-policy case (zero per-tick work, no warnings). The issue's live symptom involved a non-null policy, so this is not a regression. Noted for completeness.
  SuggestedAction: None.
  Status: pre-existing

<promise>PASS</promise>
