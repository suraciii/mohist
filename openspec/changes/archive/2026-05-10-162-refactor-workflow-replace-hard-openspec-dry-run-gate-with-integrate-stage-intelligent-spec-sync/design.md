## Context

Mohist already has an explicit INTEGRATE stage with ordered steps for `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, and `final-health`. The current spec sync implementation is still strict and programmatic: `OpenSpecIntegrator.preview()` and `apply()` parse delta markdown, validate source/target requirements, and fail on conflicts such as `missing_source` before any merge strategy can reinterpret intent.

CHECK currently wires `OpenSpecSyncDryRunCheck` into the default pre-task checks. Because failed checks are hard stage gates, a delta classification mistake such as a genuinely new requirement being placed under `MODIFIED Requirements` blocks the issue in CHECK even though the issue has not reached the point where main specs would be updated. This design keeps CHECK read-only, moves recoverable spec interpretation into INTEGRATE, and preserves post-sync validation before canonical specs are landed.

The OpenSpec reference checkout exists in `opensrc/OpenSpec/` but is empty in this worktree, so implementation should follow the issue-described opsx-sync semantics rather than importing reference code directly: read delta and main specs, merge intent intelligently, then validate the resulting main specs.

## Goals / Non-Goals

**Goals:**

- Make `openspec-sync-dry-run` non-blocking in the default CHECK path, or remove it from default CHECK while retaining optional preview evidence.
- Add an intelligent INTEGRATE spec sync path that can absorb obvious requirement-level delta classification mistakes, especially `MODIFIED` requirements with no source requirement that should be added.
- Keep `integrate:spec-sync` as a distinct task before archive, merge, and final health.
- Record every intelligent correction in task output or workflow logs; no silent markdown repair.
- Validate generated main specs after sync and fail at `integrate:spec-sync` with structured evidence if validation fails.
- Preserve existing INTEGRATE failure semantics: the issue stays in INTEGRATE and does not fall back to PLAN, BUILD, or CHECK automatically.

**Non-Goals:**

- Do not merge `integrate:spec-sync` into `integrate:archive-change`.
- Do not write `openspec/specs/` from CHECK.
- Do not introduce automatic product-code fixes, merge conflict resolution, or full pipeline reruns from spec sync failure.
- Do not implement a broad natural-language spec rewrite system; intelligent sync is limited to resolving clear OpenSpec delta intent against main spec state.
- Do not remove structural validation or allow malformed specs to land silently.

## Decisions

### D1: Treat CHECK Spec Sync As Advisory Evidence

`CheckStageRunner` should stop allowing `OpenSpecSyncDryRunCheck` failures to block the default CHECK stage. The simplest implementation is to remove `new OpenSpecSyncDryRunCheck()` from `preTaskChecks` and, if preview evidence is still needed, run it as an advisory task/check whose result is always non-failing for stage progression while preserving the original preview result in `output`.

If the advisory option is chosen, `OpenSpecSyncDryRunCheck` should distinguish transport/parser errors from preview conflicts in output fields such as `advisory: true`, `wouldPass`, `conflicts`, and `errors`, while returning `status: 'pass'` with a warning-style message for known repairable conflicts. CHECK health, AI review, merge readiness, and user approval remain blocking under the existing policy model.

**Alternatives considered:** Keep dry-run blocking and add a fix policy that rewrites delta markdown in CHECK. This was rejected because CHECK must stay read-only for main spec integration responsibilities and because delta interpretation belongs where the main spec is actually updated.

### D2: Add A Two-Layer Spec Sync Service

Keep `OpenSpecIntegrator.preview(changeDir, projectPath)` and `OpenSpecIntegrator.apply(changeDir, projectPath)` as the public interface consumed by CHECK and INTEGRATE, but split the implementation into two internal layers:

- `StrictSpecDeltaParser` or equivalent: discovers change spec files, parses delta sections, parses main specs, and produces typed `CapabilityDelta` plus `MainSpecState` without deciding recovery.
- `IntelligentSpecSynchronizer` or equivalent: resolves delta intent against main spec state, applies allowed corrections in memory, validates the resolved result, and returns a `SpecSyncSummary` with correction/audit fields.

This keeps the runner interface deep and simple while pushing classification and validation complexity behind the integrator boundary. `preview()` can use strict parsing plus advisory validation. `apply()` should use intelligent resolution, write only after the resolved state validates, and return the same summary shape extended with correction metadata.

**Alternatives considered:** Put intelligent sync logic directly in `IntegrateStageRunner`. This was rejected because the runner should orchestrate steps and evidence, not own markdown semantics. It would also duplicate parsing behavior with CHECK preview.

### D3: Resolve Only Explicit, Auditable Classification Mistakes

The first intelligent sync rule should handle the known issue class:

- If a `MODIFIED` requirement has no matching source requirement in the target main spec, and no rename maps to that source, and the target requirement name does not already exist, reinterpret that requirement as `ADDED`.

This correction should be represented in output, for example:

```json
{
  "corrections": [
    {
      "capability": "workflow-engine",
      "requirement": "REQ-WFE-005 Intelligent spec sync",
      "from": "modified",
      "to": "added",
      "reason": "missing-source-treated-as-new-requirement"
    }
  ]
}
```

Other delta types stay stricter in the first pass: `REMOVED` and `RENAMED FROM` with missing sources fail because deleting or renaming non-existent requirements is ambiguous. `ADDED` with an existing target still fails unless a future design defines safe replacement semantics. `RENAMED TO` conflicts still fail.

**Alternatives considered:** Ask an agent to freely rewrite all delta sections before applying. This was rejected because it would make validation and auditability weaker. The chosen approach handles the demonstrated failure class while keeping correction rules deterministic and explainable.

### D4: Validate The Resolved Main Spec, Not Only The Input Delta

Spec sync should build the candidate main spec content in memory, validate it, and only then write to `openspec/specs/<capability>/spec.md`. Validation should include at least:

- Requirement headers are unique in each resulting spec.
- Added or modified requirement blocks contain scenario headings.
- Delta sections are recognized and not malformed.
- The resulting markdown can be parsed back into the same requirement set.
- Target files do not contain duplicate headers after rename/modify/add operations.

If validation fails after intelligent resolution, `apply()` returns or throws a failure summary with `valid: false`, `errors`, `conflicts`, and any corrections attempted. `IntegrateStageRunner` records this as a failed `integrate:spec-sync` task and emits `integration_failed` with `failingStep: 'integrate:spec-sync'`.

**Alternatives considered:** Write the best-effort sync result and rely on later final health to catch problems. This was rejected because malformed main specs are integration data corruption and must be prevented at the spec sync boundary.

### D5: Extend SpecSyncSummary Rather Than Adding A New Evidence Store

Extend the existing `SpecSyncSummary` with optional fields such as `mode`, `advisory`, `corrections`, `validation`, and `applied`. Continue storing the summary in `StageTaskResult.output`, check output, workflow logs, and integration events. Do not create a new persistence table for spec sync.

`IntegrateStageRunner` should include these fields in the existing `specSyncOutput` for `integrate:spec-sync`, while still appending `taskId: 'integrate:spec-sync'` separately from archive and merge task results.

**Alternatives considered:** Add a dedicated `spec_sync_runs` table. This was rejected for this change because stage execution task output already provides the required audit chain and avoids another recovery surface.

### D6: Keep INTEGRATE Failure Local And Retryable

When intelligent sync or post-sync validation fails, the runner should throw from the spec sync step exactly as current INTEGRATE failures do. The stage execution should contain the failed `integrate:spec-sync` task result and event output. The workflow engine should leave the issue in INTEGRATE or interrupted/blocked-at-INTEGRATE state according to existing integrate failure handling, without stage fallback or full pipeline rerun.

Retry should be explicit: after a user or later task edits the change spec or main spec in the worktree, rerunning the INTEGRATE stage re-executes `integrate:spec-sync`. Already-completed archive/merge/final-health steps are not reached unless sync succeeds.

**Alternatives considered:** Automatically send the issue back to BUILD for spec repair. This was rejected because the failure occurs while integrating canonical specs and should be visible at the integration boundary; automatic fallback obscures the failing contract and can rerun unrelated work.

### D7: Tests Drive The Stage Boundary And Known Correction Rule

Add focused unit tests for the intelligent sync resolver and regression tests around runner semantics:

- `OpenSpecIntegrator.apply()` converts missing-source `MODIFIED` into an added requirement when safe.
- The summary records the correction and updated counts or separate correction counts clearly.
- Duplicate target, missing scenarios, malformed deltas, missing `REMOVED` sources, and ambiguous rename cases still fail.
- `OpenSpecSyncDryRunCheck` no longer blocks CHECK for missing-source preview conflicts in the default runner path.
- `IntegrateStageRunner` records failed validator output at `integrate:spec-sync` and does not run archive, merge, or final health after sync failure.
- Existing integrate/archive/merge/final-health regression tests continue to pass.

**Alternatives considered:** Only add end-to-end tests. This was rejected because the important behavior is a narrow resolver rule plus stage failure semantics; unit coverage makes the correction boundary explicit.

## Risks / Trade-offs

- [Risk] Intelligent correction may reinterpret a genuinely wrong `MODIFIED` block as a new requirement. -> Mitigation: only correct when the target requirement does not exist and there is no rename/source ambiguity; record the correction in output so users can audit it.
- [Risk] CHECK becomes less protective by not blocking on spec dry-run conflicts. -> Mitigation: keep preview evidence visible and make INTEGRATE validation strict before any archive or merge step proceeds.
- [Risk] Existing tests expect `openspec-sync-dry-run` in CHECK ordering. -> Mitigation: update ordering tests to either omit it from hard pre-task checks or assert its advisory status separately.
- [Risk] Markdown parsing remains fragile for unusual OpenSpec formatting. -> Mitigation: continue supporting the documented heading format, fail closed on malformed sections, and add fixture tests for parser edge cases.
- [Risk] Summary counts become confusing when a `MODIFIED` is applied as `ADDED`. -> Mitigation: separate original delta counts from resolved operation counts, or include a `corrections` list that makes the reinterpretation explicit.
- [Risk] The OpenSpec reference implementation is unavailable in this worktree. -> Mitigation: encode the issue-specified opsx-sync semantics in tests and keep the implementation isolated so it can be adjusted if the reference source becomes available later.

## Migration Plan

1. Update CHECK wiring so `openspec-sync-dry-run` is no longer a hard default pre-task gate; preserve optional/advisory preview output if needed by UI or logs.
2. Refactor `OpenSpecIntegrator` internals to separate parsing, resolution, validation, and writing while keeping `preview()` and `apply()` stable for callers.
3. Implement the safe `MODIFIED` to `ADDED` correction rule in the INTEGRATE apply path and record corrections in `SpecSyncSummary`.
4. Add post-resolution validation before writes and ensure failed validation returns structured errors used by `IntegrateStageRunner` output.
5. Extend `IntegrateStageRunner` spec sync output to include corrections and validation details, while preserving distinct task IDs for spec sync, archive, merge, and final health.
6. Add or update tests for advisory CHECK behavior, intelligent sync correction, validator failures, and INTEGRATE failure locality.
7. Run the existing integrate regression suite plus focused OpenSpec integrator tests.

Rollback strategy: since this change should not require schema migration, rollback is code-only. Reverting restores strict dry-run behavior and strict integrator semantics. Issues already stopped in INTEGRATE remain recoverable through the existing stage runner once code is redeployed; any corrected spec changes exist only in the issue worktree until merge lands.

## Open Questions

- Should advisory CHECK preview be shown as a passing check with warning output, or should it be removed from default CHECK and exposed only through INTEGRATE readiness details?
- Should `SpecSyncSummary` report counts by original delta sections, resolved operations, or both?
- If a `MODIFIED` requirement is missing in main but has a highly similar existing requirement name, should intelligent sync fail as ambiguous or still add a new requirement in the first implementation?
