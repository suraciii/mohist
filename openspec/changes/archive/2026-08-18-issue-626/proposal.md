## Why

Per-work resource containment and its `resourceProfile` input were removed from `core/script`, but existing `WorkflowRun` state intentionally preserves the task's original `with` declaration. A run created while that input existed can later be redelivered or enter `retrySelf` recovery and resend the retired field to the current strict Action schema, causing `core/script` to be rejected as having an unknown input and leaving recovery unable to progress. This matters now because deployments must continue recovering in-flight runs created by an earlier Runner and workflow-profile version.

## What Changes

- Add a narrowly scoped compatibility rule for historical `core/script` replay and recovery inputs that contain the retired `resourceProfile` field. The field must not affect execution or restore per-work resource containment.
- Apply the compatibility behavior consistently to direct redelivery of persisted Workflow task attempts and to recovery-generated continuation tasks, so `retrySelf` cannot reintroduce the retired input.
- Preserve all current supported `core/script` inputs (`run`, `shell`, and `timeout`) and their values, templates, task metadata, recovery budget, and completion expectations.
- Keep the current `core/script` contract strict for every other unknown input. `resourceProfile` remains unsupported in new workflow definitions and is not added back as an execution capability.
- Add regression coverage for an older persisted task carrying `resourceProfile`, its redelivery, its `retrySelf` continuation, and rejection of unrelated unknown inputs.

## Capabilities

- `workflow-replay-input-compatibility`: Replays historical Workflow task declarations against the current Action catalog without stranding valid work after a retired Action input is removed. Covers `core/script`'s retired `resourceProfile` field, direct task redelivery, recovery/self-retry continuation, preservation of supported inputs, and continued rejection of unrelated unknown fields.

## Impact

- **Workflow persistence and dispatch:** `TaskRun`/`WorkItem` input preservation and the Server's Workflow dispatch translation and redelivery paths under `packages/server/src/Mohist.Server/Workflow` and `Runner`.
- **Runner recovery and validation:** Recovery continuation construction and the execution-boundary Action validation under `packages/runner/src/runtime` and `packages/runner/src/actions`; the current `core/script` manifest and resource-containment removal remain authoritative.
- **Tests:** Focused Server dispatch/replay and Runner recovery/validator tests, plus existing built-in profile contract tests.
- **Compatibility and dependencies:** No database migration, public API, new workflow syntax, or dependency is required. Existing persisted JSON is handled at replay/recovery time; no historical run is rewritten in place.
