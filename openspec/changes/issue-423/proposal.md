## Why

A workspace that reaches `eligible` but cannot be safely removed has no exit from the cleanup loop. `safeRemove` returns `false` (its `.mohist/workspace.json` marker is missing/unreadable, or a path/marker guard refuses), the registry entry stays `eligible`, and the next tick re-evaluates the same entry and re-emits the same `refused to remove` warning — forever, across runner restarts. With retention and budget both disabled (`<= 0`) the loop should be a cheap no-op, but a single stuck entry forces the per-entry `safeRemove` path every tick. On runner `runner-pluto`, one markerless `issue-330` workspace produced 19 identical warnings in 23 minutes with zero bytes reclaimed.

## What Changes

- **Eligible entries whose removal aborts get a deterministic exit, not infinite retry.** When `safeRemove` refuses for a marker reason (missing/unreadable marker, marker `workflowRunId` mismatch) or an out-of-root reason, the registry resolves the entry so it is no longer re-evaluated as `eligible` on subsequent ticks. The concrete resolution (quarantine phase, eviction from the registry, or side-directory move) is decided in design.md; the delete guard itself is preserved either way.
- **Disabled policy stops doing work for already-observed-stuck entries.** When retention and budget are both disabled, the loop does not re-enter the per-entry `safeRemove` path on every tick for an entry that has already been observed as unresolvable.
- **Per-tick warning floods are bounded.** A permanently stuck entry stops emitting the `refused to remove` warning on every tick after the first observation.
- **Resolution survives runner restart.** The on-disk registry reflects the resolved state, so a stuck entry does not reappear as `eligible` after restart.
- **Path-guard safety is preserved.** Refusing to delete a directory outside `runnerRoot`, or whose marker `workflowRunId` mismatches the registry, remains mandatory; the change only adds an exit for the registry entry, it does not weaken the delete guard.

## Capabilities

### New Capabilities
_None._ The fix refines the existing cleanup lifecycle; it introduces no new observable surface.

### Modified Capabilities
- `runner-workspace-cleanup`: the pre-delete guard behavior and phase model change so an `eligible` entry whose removal aborts (missing/unreadable marker, marker mismatch, out-of-root path, persistent delete failure) is resolved deterministically rather than retried every tick; the retention/budget loop no longer re-evaluates already-resolved entries when policy is disabled; and per-tick warning emission is bounded. The convergence backstop (queries only `active` entries), the identity-only marker contract, and the unchanged manual `RemoveWorkspace` entry are respected.

## Impact

- **Runner** (`packages/runner/src/runtime/`): `workspace-registry.ts` (phase model / exit mutation + persistence), `cleanup-loop.ts` (`runOnce` early-return and `safeRemove` resolution path), warning emission sites in `safeRemove` and the `runCleanupOnce` log line in `host.ts`. `cleanup-convergence.ts` scope is unchanged but re-verified.
- **Tests** (`packages/runner/tests/`): `cleanup-loop-guards.spec.ts` — the test at L71 ("after guard abort, directory and entry remain intact for next tick") currently locks in the looping behavior and must be updated to the new resolution expectation; `cleanup-loop-fixture.ts` (`StubCleanupRunner`) likely extended for the resolution path. New spec coverage for cross-tick resolution, restart persistence, and disabled-policy no-op.
- **Manual path** (`workspace-removal-handler.ts`): the manual `RemoveWorkspace` handler has its own registry-drop flow; confirm it is unaffected (or align it if the resolution rule is shared).
- **Specs**: modifies `runner-workspace-cleanup` (last established by archived issue-268, widened by issue-318). No server, web, or CLI contract changes — the fix is runner-internal.
- **Risk**: low. The change is runner-internal, preserves all delete guards, and the only behavioral change visible to operators is fewer repeated warnings and stuck entries eventually leaving `eligible`.
