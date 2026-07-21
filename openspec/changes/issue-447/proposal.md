## Why

Actions currently receive a broad execution context that lets any implementation access server and runtime internals, and some OpenSpec Actions mutate workflow state outside the task-result path. With manifests now defining Action contracts, declared capabilities must become the actual boundary so workflow side effects are visible, constrained, and owned by the engine.

## What Changes

- Declare the non-default capabilities an Action requires in its manifest, including agent turns, issue-field access, workflow checkpoint identity, appending workflow tasks, and writing workflow variables.
- Replace the broad Action invocation context with a default host limited to workspace access, cancellation, logging, and command execution; inject only manifest-declared capabilities.
- Let Action manifests mark inputs whose templates must remain deferred, so an Action can carry a task template into later dispatch without receiving raw dispatch context.
- Return requested task additions and variable writes through an Action result, then have the task executor apply and report those effects through its existing engine-owned paths.
- Make the executor's promise-output projection depend on the `agent-turn` capability rather than a hard-coded Action-name list.
- Preserve the current externally observable OpenSpec follow-up tasks, variable values, OpenCode turn behavior, Action outputs, and error codes.

## Capabilities

- `action-capabilities`: Manifest-declared Action capabilities, narrow default host access, opaque issue and workflow metadata operations, deferred-template inputs, conditional capability injection, and capability-driven agent-turn completion projection.
- `action-result-effects`: Structured Action-result requests for adding tasks and writing variables, with executor-owned persistence and reporting of those effects.

## Impact

- Runner Action manifests, definitions, invocation types, built-in Actions, task and check execution, result normalization, and tests under `packages/runner/`.
- OpenSpec task loading moves from direct server mutation to the existing task-result reporting path; immediate variable persistence moves behind the executor.
- No new external plugin model, process isolation, permission sandbox, Action input/output contract, or runtime dependency is introduced.
