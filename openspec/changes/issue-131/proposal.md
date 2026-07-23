## Why

Workflow profiles currently repeat an Agent's long-lived instructions, runtime, model, and execution configuration in every inline task. A Workflow Action that references a named Mohist Agent makes those reusable role definitions available to workflow tasks now that Agent definitions exist, while preserving WorkflowRun as the authority for workflow work.

## What Changes

- Add the `mohist/agent` Workflow Action, with required Agent `name` (or id) and task `prompt`, plus optional `session` and `timeout` inputs.
- Resolve the referenced active Agent at each task dispatch and freeze its instructions, runtime, model, and execution configuration for that attempt; a retry resolves the current definition again.
- Combine the Agent's long-lived instructions/configuration with the task's per-run prompt without allowing the Agent definition to replace the task's workflow goal.
- Report a missing or archived referenced Agent as the actionable `agent_not_found` task-dispatch failure; profile save and validation only validate the Action input shape.
- Execute through the selected runtime using the existing Workflow task and Workflow-origin AgentSession path. Do not create an AgentJob or direct AgentSession, and retain task results, checks, recovery, and state advancement in the WorkflowRun.

## Capabilities
- `workflow-agent-action`: Workflow profile authors can reference a project Agent definition from a task, receive a dispatch-time execution snapshot, and retain normal Workflow task lifecycle and failure semantics.

## Impact

- Server Workflow-to-Runner dispatch translation and Action catalog validation must resolve Agent definitions through an Agent read-side boundary without making the Workflow domain depend on Agent entities.
- Runner Action catalog and runtime Action invocation must accept the resolved Agent snapshot and reuse the existing OpenCode or Pi workflow execution paths.
- Workflow profile YAML users gain `uses: mohist/agent`; existing inline `mohist/opencode` and `mohist/pi` tasks remain unchanged.
