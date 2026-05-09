## Why

Mohist currently treats OpenSpec sync dry-run failures as a hard CHECK gate, so repairable delta classification mistakes such as a new requirement being written as `MODIFIED` can block an issue before the system has a chance to intelligently integrate the intended spec change. Moving intelligent spec synchronization into INTEGRATE aligns Mohist with OpenSpec's agent-driven sync model while preserving validation and auditability at the point where main specs are actually updated.

## What Changes

- Downgrade `openspec-sync-dry-run` in CHECK from a hard blocking gate to an advisory preview or remove it from the default CHECK blocking path, so CHECK verifies candidate implementation/spec consistency without modifying main specs or stopping on repairable delta-shape errors.
- Upgrade `integrate:spec-sync` from strict programmatic delta application to an agent-driven intelligent sync task that reads change delta specs and main specs, interprets `ADDED`, `MODIFIED`, `REMOVED`, and `RENAMED` requirements, and can absorb obvious delta classification mistakes.
- Keep `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, and `final-health` as distinct integration steps with separate task history, logs, and failure evidence.
- Validate synchronized main specs after intelligent sync so malformed output, duplicate requirement headers, unresolved delta problems, or validator failures stop integration with clear evidence instead of silently landing in `openspec/specs/`.
- Preserve failure semantics where spec sync failures remain in INTEGRATE with a specific failing step and auditable output, without falling back to PLAN, BUILD, or CHECK and without automatically rerunning the whole pipeline.
- Add regression coverage for #159/#160-style cases where an intended new requirement is incorrectly represented as `MODIFIED` and should be integrated as an addition when the main spec has no matching source requirement.

## Capabilities

### New Capabilities


### Modified Capabilities

- `pipeline-model` — CHECK and INTEGRATE stage responsibility boundaries change so CHECK no longer hard-blocks on repairable OpenSpec dry-run delta classification errors, while INTEGRATE owns durable spec synchronization and integration failure semantics.
- `workflow-definition` — OpenSpec workflow behavior changes to keep spec sync separate from archive/merge/final health and to make intelligent sync an explicit integration task rather than an implicit or CHECK-stage gate.
- `workflow-engine` — Check results remain read-only evidence, while integration task execution must support intelligent spec sync, structured validation, and stage-local failure without cross-stage fallback.
- `change-artifacts` — Task output and workflow logs must preserve advisory spec preview evidence, intelligent sync correction details, validator results, and failed sync evidence without treating transient logs as durable artifacts.

## Impact

- **Check stage**: `packages/cli/src/workflow/check-stage-runner.ts` and `packages/cli/src/workflow/checks/openspec-sync-dry-run-check.ts` need updated default behavior so OpenSpec sync preview is advisory/non-blocking or no longer part of the hard CHECK gate.
- **Integrate stage**: `packages/cli/src/workflow/integrate-stage-runner.ts` must keep `integrate:spec-sync` as the first distinct integration step, record task output for any intelligent corrections, and stop at that step on sync or validation failure.
- **OpenSpec sync services**: `packages/cli/src/openspec/open-spec-integrator.ts` and related parser/validator code need an intelligent sync path that can compare delta specs with `openspec/specs/`, repair obvious requirement-level classification mismatches, apply the resolved result, and validate the final main spec structure.
- **Agent prompts/templates**: The integration prompt or task template should reference the OpenSpec opsx-sync agent-driven semantics from `opensrc/OpenSpec/src/core/templates/workflows/sync-specs.ts` while preserving Mohist's structured task output and validation contract.
- **Workflow evidence and events**: Stage task results, workflow logs, and integration events must distinguish advisory CHECK preview output from INTEGRATE sync/apply output, archive output, merge output, and final health output.
- **Tests**: Regression tests should cover CHECK non-blocking behavior for missing source requirements, intelligent sync of `MODIFIED`-but-actually-new requirements, post-sync validation failures, and INTEGRATE failure recovery that remains at `integrate:spec-sync` without reverting to earlier stages.
