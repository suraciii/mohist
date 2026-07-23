## Why

Workflow profiles currently repeat an Agent's long-lived instructions, runtime, model, and execution configuration in every inline task. A Workflow Action that references a named Mohist Agent makes those reusable role definitions available to workflow work now that Agent definitions exist, while preserving WorkflowRun as the authority for that work. The task-only product contract is already finalized in `docs/actions/agent.md`; this change lands it.

## What Changes

- Add the `mohist/agent` Workflow Action, with required static Agent `name` (or id) and template-renderable workflow `prompt`, plus optional `session` and `timeout` inputs, for tasks. It is rejected for checks; check support is a separate contract change and is out of scope. `agent_*` references resolve by id only; all other references resolve by name before an id fallback.
- Resolve the referenced active Agent while creating a task attempt dispatch snapshot. Persist the concrete transformed `WorkDispatch` on that active WorkflowRun work before it can be offered; reoffers return it verbatim after restarts, and a retry creates a new snapshot from the current definition.
- Compose the Agent's long-lived instructions ahead of the task's raw `prompt` into a single dispatch prompt at the server, without allowing the Agent definition to replace the workflow work goal. Template expressions in the raw prompt are still rendered by the Runner. The selected runtime receives the existing published input contract `{ prompt (composed), session?, timeout?, options: { model?, variant? } }`; no new `options` key is introduced.
- Report a missing or archived referenced Agent as the actionable structured `agent_not_found` dispatch failure: a failed `TaskReport` with `ExecutionError`. Profile save and validation only validate the Action input shape.
- Execute through the selected runtime using the existing Workflow task and Workflow-origin AgentSession path. Do not create an AgentJob or direct AgentSession, and retain results, recovery, and state advancement in the WorkflowRun.

## Capabilities
- `workflow-agent-action`: Workflow profile authors can reference a project Agent definition from a task, receive a durable attempt execution snapshot, and retain normal Workflow lifecycle and failure semantics.

## Impact

- Server Workflow-to-Runner dispatch translation and Action catalog validation must resolve Agent definitions through an Agent read-side boundary without making the Workflow domain depend on Agent entities, compose instructions into the dispatch prompt, and persist transformed task envelopes on the active work.
- The Runner is unchanged: the transformed envelope uses the existing published input contract of `mohist/opencode` or `mohist/pi`, including their existing timeout behavior and their existing handling of unknown `options` keys.
- Workflow profile YAML users gain `uses: mohist/agent` in tasks; existing inline `mohist/opencode` and `mohist/pi` work remains unchanged.
- `docs/actions/agent.md` is already task-only and requires no scope expansion; only minimal reconciliation wording is applied if needed.
