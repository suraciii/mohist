# `mohist/agent` Action

`mohist/agent` launches a predefined Mohist Agent from the Project for one
Workflow task. The task enters the canonical AgentJob launch boundary and
creates a real AgentJob and AgentSession. AgentJob owns execution, retry,
recovery, and result. AgentSession owns conversation continuity. Neither owns
Workflow or Approval Point state. A missing, archived, or not-ready Agent fails
launch explicitly. This Action supports tasks only, not Workflow checks.

This is the only Agent task binding in a Workflow Profile. Runtime Actions such
as `mohist/opencode` and `mohist/pi` are internal Agent-to-Runner contracts
selected by the Agent definition; a Profile `uses` cannot reference them. See
[`../../design/decisions/workflow-agent-binding.md`](../../design/decisions/workflow-agent-binding.md)
for the binding decision.

`with` requires `name` and `prompt`. It also accepts optional `session` and
`timeout` inputs.

See [Agents and AgentSessions](../agent-sessions.md) for the overall
relationship between Agent, AgentJob, and AgentSession.

## Basic Usage

```yaml
- id: review
  uses: mohist/agent
  with:
    name: reviewer
    prompt: ${{ prompts.review }}
```

The Agent selected by `name` provides identity instructions, execution backend
(OpenCode, Pi, or Codex), model, optional Reasoning Effort, true model variant,
and Skills. `prompt` is the input for this task. Use this Action when multiple
Tasks or Profiles reuse the same role, or when routing rules and `@` mentions
must use the same Agent identity. Runtime-specific Actions (`mohist/opencode`,
`mohist/pi`) are no longer selectable from a Profile, and Codex adds no
user-selectable Runtime Action. The Agent definition owns the backend choice.

## Action Inputs

- `name` (required): static Mohist Agent name or ID. Template expressions are
  not supported.
- `prompt` (required): task input for the Agent. Template expressions are
  supported.
- `session` (optional): explicit logical Session name within the WorkflowRun.
  The same name continues the logical Session only when the Agent and Workspace
  identities also match. When omitted, the Task requests no named Session
  reuse.
- `timeout` (optional): deadline for this execution in milliseconds. When
  omitted, the Agent definition supplies its configured default deadline; the
  Workflow does not insert one.

The Agent configuration selects the execution backend, model, optional
Reasoning Effort, true model variant, and Skills. Reasoning Effort is one of
`off`, `minimal`, `low`, `medium`, `high`, `xhigh`, or `max`; it is independent
from Variant. OpenCode does not support an explicit Reasoning Effort, so choose
Pi or Codex, or leave the effort unset for OpenCode. The task cannot override
these values. `prompt` supplies only the goal for this work and cannot modify
the Agent definition. Task-level constructs such as `expect`, `artifacts`,
`setVars`, and recovery behave as they do for other Actions.

`name` uses the same resolution order as the `mo` command surface. A reference
that starts with `agent_` resolves only as an ID. Other references resolve by
name first and fall back to ID when no name matches.

## Resolution and Snapshot

- Each AgentJob launch resolves `name` to a snapshot of the current definition.
  The instructions, execution backend, model, Reasoning Effort, true model
  variant, and ordered Skills remain fixed for that AgentJob.
- Editing the Agent does not affect an accepted AgentJob. A Workflow retry is a
  new launch, so a repaired definition takes effect on the new AgentJob.
- An ordinary client may provide a prompt and context. It cannot use task input
  or context to select a different Runtime, model, Reasoning Effort, Variant, or
  set of Skills.
- Pi thinking-level values previously saved as `variant` are not migrated or
  reinterpreted. Re-enter the value as `reasoningEffort` in the Agent profile;
  until then, the saved configuration is rejected explicitly.
- Profile save and `mo workflow validate` check only the input shape and require
  `name` and `prompt`. They do not check whether the Agent exists, so Agent
  creation and removal do not block the Profile lifecycle.

## Failure Semantics

The Action defines two business error codes:

- `agent_not_found`: `name` does not resolve to an existing Agent at launch
  time, or the Agent is archived.
- `agent_not_ready`: the Agent exists but has unresolved readiness gaps. The
  error message lists the gaps.

A named Session can be reused only with the same Agent and Workspace. A name
that is already bound to a different Agent in the same Run fails launch with
`workflow_session_agent_conflict`. Execution errors such as backend
unavailability and timeout are the same as for the backend Action the Agent
selects. Recovery `when` matching applies in the same way.

`mohist/agent` can be used only for a task and is rejected when used for a
check. If the referenced Agent does not exist or is archived, dispatch fails
with `agent_not_found`.
