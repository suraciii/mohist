## Why

Workflow failure handling currently implements retry-target selection and recovery-budget bounds in more than one place. Those duplicated rules have already diverged, risking a status view that disagrees with what a retry can perform and a control plane that contradicts runner-owned recovery semantics.

## What Changes

- Use one retry-target determination for failed task and check work, shared by retry execution and the available-actions status view.
- Preserve the existing retry, rerun, and rerun-from-stage behavior while ensuring every retryable failure is represented consistently to users and controls.
- Keep recovery-budget bounds exclusively in the runner's recovery evaluation: the control plane accepts structurally valid recovery continuation state and transports it without enforcing declared-budget ranges.
- Preserve the immutable recovery declaration and the existing rule that a manual retry starts a fresh recovery round.

## Capabilities

- `workflow-task-recovery`: Failed workflow work exposes retry availability from the same retry target used for execution, and recovery continuation state preserves the runner as its sole budget-bound authority.

## Impact

- **Server:** WorkflowRun failure and retry decisions, workflow status/action mapping, and runtime follow-up task ingestion.
- **Runner:** Existing recovery evaluation remains the sole component that clamps recovery remaining allowance to the declared budget.
- **Tests:** Workflow failure/retry and recovery-continuation coverage must prove status and execution agree, and that structurally valid out-of-range continuation values reach the runner-owned evaluation path.
- **APIs and dependencies:** No public API, CLI, persistence-schema, or dependency changes; normal workflow control behavior remains compatible.
