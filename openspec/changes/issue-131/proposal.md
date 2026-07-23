## Why

Workflow profiles currently repeat an Agent's long-lived instructions, runtime, model, and execution configuration in every inline task or check. A Workflow Action that references a named Mohist Agent makes those reusable role definitions available to workflow work now that Agent definitions exist, while preserving WorkflowRun as the authority for that work.

## What Changes

- Add the `mohist/agent` Workflow Action, with required Agent `name` (or id) and workflow `prompt`, plus optional `session` and `timeout` inputs, for both tasks and checks.
- Resolve the referenced active Agent while creating a task or checks attempt dispatch snapshot. Persist the concrete transformed `WorkDispatch` on that active WorkflowRun work before it can be offered; reoffers return it verbatim after restarts, and a retry creates a new snapshot from the current definition.
- Combine the Agent's long-lived instructions/configuration with the task or check's rendered prompt without allowing the Agent definition to replace the workflow work goal. The selected runtime receives `options.instructions`, `options.model`, and `options.variant`; the runtime Action validates and applies that closed payload.
- Report a missing or archived referenced Agent as the actionable structured `agent_not_found` dispatch failure: a failed `TaskReport` with `ExecutionError` for tasks, or an equivalent named failed `CheckResult` for checks. Profile save and validation only validate the Action input shape.
- Execute through the selected runtime using the existing Workflow task/check and Workflow-origin AgentSession path. Do not create an AgentJob or direct AgentSession, and retain results, recovery, checks, and state advancement in the WorkflowRun.

## Capabilities
- `workflow-agent-action`: Workflow profile authors can reference a project Agent definition from a task or check, receive a durable attempt execution snapshot, and retain normal Workflow lifecycle and failure semantics.

## Impact

- Server Workflow-to-Runner dispatch translation and Action catalog validation must resolve Agent definitions through an Agent read-side boundary without making the Workflow domain depend on Agent entities, and persist transformed task and check envelopes on the active work.
- Runner Action catalog and runtime Action invocation must accept the closed resolved Agent payload and reuse the existing OpenCode or Pi workflow execution paths, including their timeout behavior.
- Workflow profile YAML users gain `uses: mohist/agent` in tasks and checks; existing inline `mohist/opencode` and `mohist/pi` work remains unchanged.
