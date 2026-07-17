## Context

The runner's automatic workspace-cleanup loop (`packages/runner/src/runtime/cleanup-loop.ts`) reclaims workspace directories once their owning workflow run is terminal. An `eligible` registry entry is evicted by the retention pass (age) and/or budget pass (disk usage). Before deleting, `safeRemove` runs three pre-delete guards: path must be under `runnerRoot`, the `.mohist/workspace.json` marker must be readable, and its `workflowRunId` must match the registry entry. If any guard refuses, `safeRemove` returns `false` and **the entry stays `eligible`** — so the next tick (every `cleanupLoopIntervalMs`, default 2 min) re-evaluates the same entry, re-runs the guards, and re-emits the same `refused to remove` warning. Forever, across runner restarts (the registry persists to `<runnerRoot>/.mohist/runner-state/workspaces.json`).

This produced the issue-423 symptom: one markerless `issue-330` workspace emitted 19 identical warnings in 23 minutes with zero bytes reclaimed. The guards are correct (refusing to delete an unidentifiable directory is the safe behavior); the gap is that a guard-refused entry has **no exit** from `eligible`.

Current phase model is closed: `WorkspaceRegistryPhase = "active" | "eligible"`. The only way out of `eligible` is `registry.remove()`, which `safeRemove` refuses to call on a guard refusal. So a marker-missing / out-of-root / marker-mismatch entry is permanently `eligible`.

The convergence backstop (`cleanup-convergence.ts:48`) queries the server **only** for `active` entries by design (locked by `QueriesOnlyActiveEntries_IgnoresEligible`), so it never comes to the rescue of a stuck `eligible` entry.

Motivation and required behavior are in `proposal.md`; normative requirements and scenarios are in `specs/runner-workspace-cleanup/spec.md`.

## Goals / Non-Goals

**Goals:**
- Give a guard-refused `eligible` entry a deterministic, persisted exit so it is never re-evaluated or re-warned on subsequent ticks.
- Bound warning emission to a single observation per stuck entry.
- Make resolution independent of the retention/budget policy (a stuck entry is resolved even when both policies are disabled), satisfying spec scenario "Resolution occurs even when both retention and budget are disabled."
- Preserve every pre-delete safety guard — the directory is never deleted when a guard refuses.
- Make the fix runner-internal: no server / web / CLI contract change.

**Non-Goals:**
- Resolving *delete* failures (the `safeRemove` catch block). A failed `deleteDirectory` may be transient (EBUSY/EPERM) and should remain retryable; distinguishing "persistent" delete failure needs a retry budget — out of scope (see Open Questions).
- Reclaiming the stuck directory's disk. The directory is intentionally left in place for operator/manual attention; this change only stops the loop from nagging about it.
- Changing the marker file format or write path.
- The dispatch re-render storm (separate issue #424).
- Changing retention/budget policy semantics or defaults.

## Decisions

### D1: Resolve via a new terminal `stuck` phase (not eviction, not quarantine)

Add `"stuck"` to `WorkspaceRegistryPhase` (`"active" | "eligible" | "stuck"`). When the cleanup loop's guards refuse an `eligible` entry, transition it `eligible → stuck` via a new idempotent `registry.markStuck(workflowRunId)`, persisted through the existing atomic write-through. A `stuck` entry is excluded from both loops automatically: cleanup filters `phase === "eligible"`, convergence filters `phase === "active"` — no filter changes needed. The directory is **not** deleted, moved, or touched.

The phase transition is also the structural warning de-duplication: once `stuck`, the entry is never re-evaluated, so the refusal warning fires exactly once (at the transition). No separate rate-limiter / log-throttle state machine is required.

`loadFromDisk` validation (`workspace-registry.ts:230`) is widened to accept `"stuck"` so the state survives restart consistently (a stuck entry reloads as stuck, not silently dropped).

**Alternatives considered:**
- *Evict the entry (`registry.remove`) on guard refusal.* Simplest and least code, and behaviorally satisfies the spec (entry gone ⇒ not re-evaluated, not re-warned, persists). Rejected because it discards the ownership record: the runner would "forget" a directory it materialized, leaving an invisible orphan. Operators lose the queryable "needs attention" list that motivated the issue, and the registry stops tracking a directory that still exists on disk — conflicting with the registry's documented ownership purpose.
- *Quarantine (move the directory under a side dir like `.mohist/quarantine/`).* Heaviest. Moving a directory the guards just refused to touch is risky (the path may be out-of-root or unwritable), adds a filesystem mutation, and is over-engineering for a problem that is fundamentally "stop retrying." Rejected.

### D2: Resolution is an eager, policy-independent pass at the top of `runOnce`

Insert a resolution pass immediately after the `eligible.length === 0` early-return and **before** the `retentionDisabled && budgetDisabled` early-return (`cleanup-loop.ts:51`). For each `eligible` entry it evaluates the guards; on refusal it warns once + `markStuck` + increments a counter. This makes resolution independent of policy (required by the spec) and means a stuck entry is resolved on the first tick it is seen, regardless of whether retention/budget are enabled.

After the pass, remaining `eligible` entries are exactly those that passed the guards, so the retention and budget passes (which re-list `eligible`) operate only on cleanly removable candidates. `safeRemove` keeps its guard checks as a defensive race backstop (a marker could be deleted between the resolution pass and the eviction pass); in normal operation that branch is unreachable because the entry is already `stuck`.

**Alternative considered:** resolve inline only when `safeRemove` is called during eviction (policy-gated). Simpler, but resolution would not happen when both policies are disabled — contradicting the spec's policy-independence scenario — and would delay detection of stuck entries until they cross the retention/budget threshold. The eager pass is a one-time cost per entry (resolved entries leave `eligible`) and the per-tick cost is O(eligible) cheap marker reads.

### D3: Extract guard evaluation into a shared helper

Pull the three guard checks (path containment, marker read, marker match) out of `safeRemove` into a private `evaluateGuards(entry): Promise<{ ok: true } | { ok: false, message: string }>`. Both the resolution pass and `safeRemove` call it, avoiding duplicated guard logic and keeping `safeRemove` self-contained for its direct unit tests (which assert the warning and that the directory is left intact when called directly).

### D4: `markEligible` only transitions `active → eligible` (correctness fix)

Today `markEligible`'s idempotency guard is `if (existing.phase === "eligible") return`. With a `stuck` phase, a redelivered terminal workflow-status event (SignalR may redeliver) would call `markEligible` on a `stuck` entry and — because `stuck !== "eligible"` — proceed to set `phase = "eligible"`, **reviving** the stuck entry and restarting the warning loop. Change the guard to `if (existing.phase !== "active") return { ...existing }`: only `active` entries transition to `eligible`; `eligible` and `stuck` are returned unchanged. This is strictly more correct and also guards any future terminal phase.

### D5: New `stuckResolved` counter + log surface

Add `stuckResolved: number` to `CleanupLoopResult`. Update `runCleanupOnce` in `host.ts` so the summary log includes `stuck=${result.stuckResolved}` and the "log if any counter > 0" condition includes `stuckResolved`. This keeps operator visibility (one summary line per tick that resolved something) without per-tick per-entry noise.

### D6: Manual `RemoveWorkspace` path is unchanged

`workspace-removal-handler.ts` does **not** call `safeRemove`; it resolves the entry by path via `findByWorkspacePath` + `registry.remove`, phase-agnostically. It therefore already handles `stuck` entries correctly (an operator can clean up a stuck directory through the manual entry). No change needed; called out to prevent conflating the two paths.

## Risks / Trade-offs

- [Stuck entries accumulate in the registry forever] → Mitigation: bounded by the (rare) rate of marker loss / path corruption; the directory is not deleted so there is no new disk growth versus today; an operator can clean a stuck entry via the manual `RemoveWorkspace` path, which drops the entry and deletes the directory. If accumulation ever matters, a separate "GC stuck entries older than N" can be added later (non-goal here).
- [Resolution pass adds a marker read per eligible entry per tick, including when both policies are disabled] → Mitigation: one-time per entry — once resolved it leaves `eligible`; the steady-state eligible set is small (recently-terminal workspaces awaiting eviction); a marker read is a single small JSON file. Net per-tick cost is negligible vs. today.
- [Delete-failure entries (`safeRemove` catch) still retry every tick when policy is enabled] → Mitigation: this is pre-existing, unchanged behavior, and a different failure mode than the guard refusals this change targets; transient delete failures should remain retryable. Flagged as an open question for a future retry budget, not handled here.
- [Race: marker deleted between the resolution pass and the eviction pass] → Mitigation: `safeRemove` re-checks the guards defensively and returns `false` (entry stays `eligible`); the next tick's resolution pass marks it `stuck`. At worst a rare, single extra warning — acceptable.
- [Rollback after stuck entries were persisted] → Mitigation: the pre-change `loadFromDisk` validation skips any phase that is not `active`/`eligible`, so on rollback stuck entries are silently dropped from the in-memory registry (directories remain on disk, untracked) — equivalent to the eviction alternative. No corruption, no hazard.

## Migration Plan

- **No data migration.** Existing `workspaces.json` files contain only `active`/`eligible` entries; the widened load validation accepts them unchanged plus the new `stuck` value.
- **Self-healing on deploy.** A runner currently stuck on a markerless entry (e.g. `issue-330`) will, on its first cleanup tick after redeploy, run the resolution pass, mark the entry `stuck`, emit its final warning, and stop. The symptom clears without operator action.
- **Rollback.** Revert the code. Persisted `stuck` entries are skipped by the old load validation (treated as absent); directories are left on disk for manual cleanup. Safe.

## Open Questions

- Should *persistent* `deleteDirectory` failures eventually be resolved (e.g. after N failed attempts → `stuck`)? Needs a retry-budget / attempt-counter field on the entry. Deferred — the issue's symptom and acceptance criteria are about deterministic guard refusals, and conflating transient failures risks giving up on recoverable deletes.
- Should a `stuck` entry carry a `stuckAt` timestamp and/or `stuckReason` for richer diagnosis? Kept out to honor the minimal-model principle; the single transition-time warning already carries the reason and path. Revisit if operability demands a queryable stuck table.
- Should the Web/CLI surface stuck entries to operators (e.g. a "needs attention" list)? Not required by this change (runner-internal), but the `stuck` phase makes it cheap to add later.
