# Review Report

## Result: FAIL

This review evaluates the **post-build candidate snapshot** (merge-base `3a8b96dd`..HEAD), not the plan. The plan (proposal / spec / design / tasks) was approved at the Plan stage; this review checks whether the built code satisfies the issue's acceptance criteria.

The change ships only **T-001** (the registry data-model foundation: the `"stuck"` phase, `markStuck`, widened load validation, and the `markEligible` tightening of D4). **T-002 — the actual bug fix in the cleanup loop — is entirely absent from the candidate.** The result is that `markStuck` is dead code (zero callers), the cleanup loop behaves exactly as before the change, the issue's symptom is unmitigated, and 4 of 5 acceptance criteria are unmet.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/runner/src/runtime/cleanup-loop.ts`, `packages/runner/src/runtime/host.ts`, `packages/runner/src/runtime/workspace-registry.ts`
  Evidence: The issue's bug ("a permanently stuck `eligible` workspace re-evaluates and re-warns every tick, forever") is **not fixed**. Concretely:
    - `markStuck` is defined at `workspace-registry.ts:171` but has **zero non-test callers** in the entire repo (`rg -n "markStuck" --glob '!openspec/**'` returns only the definition + tests in `workspace-registry.spec.ts`). Nothing in the cleanup loop, host, or anywhere else ever transitions an entry to `stuck`.
    - `cleanup-loop.ts` is byte-for-byte unchanged from the merge base: `runOnce` still lists `phase === "eligible"` (L45), still early-returns on `retentionDisabled && budgetDisabled` only after listing eligible (L46-51), and `safeRemove` (L114-144) still returns `false` on a guard refusal and leaves the entry `eligible`. There is no resolution pass, no `evaluateGuards` helper, and no `markStuck` call.
    - `CleanupLoopResult` (`cleanup-loop.ts:14-19`) still has only `retentionRemoved / budgetRemoved / guardAborted / workspaceUsageBytes` — no `stuckResolved` counter (design D5, T-002 AC).
    - `host.ts:357-359` log surface is unchanged: the condition is `result.retentionRemoved > 0 || result.budgetRemoved > 0 || result.guardAborted > 0` and the line is `retention=.. budget=.. guardAborted=.. usage=..` — no `stuck=` surface.
    - Therefore a markerless `eligible` entry still produces the exact symptom from the issue body every tick, across restarts. The `19 warnings in 23 minutes` flood is unaddressed.

  Acceptance-criteria trace against the **built** candidate:
    - AC#1 (stops warning every tick after first observation) — **UNMET**. The warning is still emitted on every tick; no resolution path exists.
    - AC#2 (resolved deterministically; no longer re-evaluated as `eligible`) — **UNMET**. No transition out of `eligible` exists for guard refusals.
    - AC#3 (disabled policy stops doing work every tick) — **UNMET**. The early-return ordering and the absence of a policy-independent resolution pass mean a stuck entry keeps re-entering `safeRemove`.
    - AC#4 (path-guard safety preserved) — **MET** (vacuously; the guards in `safeRemove` are untouched).
    - AC#5 (does not survive restart into same stuck state) — **NOT REACHABLE**. `loadFromDisk` does round-trip `stuck` (L250, T-001), but since nothing ever *writes* a `stuck` entry, this code path is dead and the criterion cannot be satisfied in practice.

  [disallowed:reason] Repair was considered (implement the resolution pass + `evaluateGuards` + `stuckResolved` per design D2/D3/D5 and T-002). This is a product-behavior change touching the cleanup loop's control flow and a public-ish log surface — explicitly disallowed by the review repair policy ("product behavior changes", "architectural judgment"). Reported unresolved.

  SuggestedAction: Implement T-002 as specified in `tasks.json` and design D2/D3/D5: extract `evaluateGuards`, add the eager policy-independent resolution pass at the top of `runOnce` (after the empty-eligible early-return, **before** the `retentionDisabled && budgetDisabled` early-return) that calls `markStuck` on a guard refusal, add `stuckResolved` to `CleanupLoopResult`, and surface `stuck=N` in `host.ts`'s summary log and its log condition. Then the four unmet ACs are satisfied by the same mechanism.
  Verification: After T-002, run `npm test -w packages/runner`; the updated `cleanup-loop-guards.spec.ts` should assert a single warning + a `stuck` entry across two ticks, and the disabled-policy scenario should resolve. `rg -n "markStuck" packages/runner/src` must show a non-test caller in `cleanup-loop.ts`.
  Status: unresolved

- [ID: item-2]
  Severity: blocking
  Scope: `packages/runner/tests/cleanup-loop-guards.spec.ts:71` and the absence of spec-scenario coverage
  Evidence: The pre-existing test `"after guard abort, directory and entry remain intact for next tick"` (L71-91) is **unchanged** and still asserts the exact looping behavior the issue is filed to eliminate: it runs `runOnce` twice and asserts the **same** `refused to remove` warning is emitted on **both** ticks (L78-81 and L85-89) and that the entry remains present/non-stuck after tick 2. As long as this test passes, the bug is present. No test in the candidate covers any of the five scenarios in `specs/runner-workspace-cleanup/spec.md` (missing/unreadable marker resolution, mismatch resolution, out-of-root resolution, no-re-attempt/no-re-warn on subsequent ticks, disabled-policy resolution, restart-persistence). The T-001 registry tests prove `markStuck` works in isolation but prove nothing about the loop that is supposed to drive it.
  [disallowed:reason] Updating this test to the new resolution semantics is inseparable from implementing T-002 (the behavior it asserts does not exist yet) — disallowed as a product-behavior change.
  SuggestedAction: Land together with T-002 (item-1): rewrite this test to assert a single warning + `phase === "stuck"` after tick 1 and no re-warn on tick 2, and add the missing scenario coverage listed in T-002's acceptance criteria.
  Verification: `npm test -w packages/runner -- cleanup-loop-guards`; the looping assertion must be gone and the resolution scenarios must pass.
  Status: unresolved

## Minor Items

- [ID: item-3]
  Severity: minor
  Scope: `packages/runner/src/runtime/workspace-registry.ts:171-179` (`markStuck`)
  Evidence: Design D1 and T-001 both specify `markStuck` as an `eligible → stuck` transition, but the implementation only short-circuits `phase === "stuck"`; it will happily transition `active → stuck` (and any future terminal phase). This is a contract discrepancy vs. the spec/design. It is currently harmless because the method has no caller (see item-1), but if T-002 is wired to call `markStuck` directly off the eligible list the contract should match. No code change recommended in isolation; flag for the T-002 implementer to either add an `if (existing.phase !== "eligible") return { ...existing }` guard or document the broader contract.
  SuggestedAction: Decide and align the `markStuck` precondition with design D1 when wiring T-002.
  Verification: Unit test asserting `markStuck` on an `active` entry is a no-op (or documents the intended behavior).
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `openspec/changes/issue-423/tasks.json` (`"passes": false` flags), progress tracking
  Evidence: `tasks.json` marks **both** T-001 and T-002 as `"passes": false`, yet T-001's code and tests are present and passing (`npm test -w packages/runner -- workspace-registry` → 28 passed; `npm run typecheck -w packages/runner` → clean). The progress signal is therefore stale and does not distinguish "T-001 built & green" from "T-002 not started". With the workflow showing the build stage producing a candidate, this risks the check/integrate stages mis-reading completion. Low impact on this review's verdict, but a traceability risk.
  SuggestedAction: After T-001 is confirmed built and green, flip its `passes` to `true` (and keep T-002 `false` until its code lands) so progress state reflects reality.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/runner/src/runtime/cleanup-loop.ts:114` (`safeRemove` delete-failure catch, L140-143)
  Evidence: `safeRemove`'s catch block swallows a `deleteDirectory` failure and leaves the entry `eligible`, so a *persistently* failing delete (e.g. chronic EBUSY/EPERM) also loops forever — same shape as the guard-refusal bug this issue targets, but a different root cause. Design Open Questions explicitly defers this (needs a retry budget) and it is correctly out of scope here; recorded so it is not forgotten.
  SuggestedAction: Future issue — add an attempt counter / retry budget on the entry and resolve to `stuck` (or a dedicated state) after N failed deletes.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: `packages/runner/src/runtime/cleanup-loop.ts:45-51` (disabled-policy early-return placement)
  Evidence: The issue body's "layered violation #2" claims the `retentionDisabled && budgetDisabled` early-return is only reached when `eligible.length === 0`. Reading the built code, the early-return at L51 fires whenever both policies are disabled regardless of eligible count — so when policy is fully disabled the loop already does *no per-entry work* today (it lists eligible then returns before any `safeRemove`). The live symptom therefore requires at least one enabled policy selecting the stuck entry for eviction. The self-review (item-2) flagged this correctly. T-002's policy-independent resolution pass still satisfies AC#3 and is worth doing, but the issue's framing of violation #2 is slightly inaccurate vs. the code. No action required; noted for accuracy.
  SuggestedAction: None.
  Status: pre-existing

## Verification performed

- `npm run typecheck -w packages/runner` — passes (clean).
- `npm test -w packages/runner -- --run workspace-registry cleanup-loop-guards` — 2 files, 28 tests pass (the candidate's own tests are green; they do not exercise the bug fix).
- `rg -n "markStuck" --glob '!openspec/**'` — only the definition + `workspace-registry.spec.ts` callers; **no production caller**.
- `git diff --stat $(git merge-base origin/master HEAD)..HEAD` — candidate touches only `workspace-registry.ts`, its test, and `openspec/changes/issue-423/*`; `cleanup-loop.ts`, `host.ts`, `cleanup-loop-guards.spec.ts`, and `cleanup-loop-fixture.ts` are **not** in the diff.

The candidate builds and its own tests pass, but it does not implement the fix the issue requires.

<promise>FAIL</promise>
