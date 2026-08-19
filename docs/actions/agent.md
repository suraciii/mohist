# `mohist/agent` Action

`mohist/agent` lets a Workflow task execute with a predefined Mohist Agent from
the Project. The task receives a snapshot of that Agent's instructions and
execution configuration, then runs through the same mechanism as an Inline
Agent. It supports tasks only, not Workflow checks.

This is an **Agent definition reference, not a work delegation**. It does not
start an AgentJob. TaskRun still decides whether the work succeeds or fails,
and the AgentSession still has a Workflow origin. See
[Agents and AgentSessions](../agent-sessions.md) for the overall relationship
between Agent, AgentJob, and AgentSession.

## Basic Usage

```yaml
- id: review
  uses: mohist/agent
  with:
    name: reviewer
    prompt: ${{ prompts.review }}
```

The Agent selected by `name` provides identity instructions, execution backend
(OpenCode or Pi), model, optional Reasoning Effort, true model variant, and
Skills. `prompt` is the input for this task. Use this Action when the same role
must be reused by multiple tasks or Profiles, or when routing rules and `@`
mentions must use the same Agent identity. Continue to use
[`mohist/opencode`](opencode.md) or [`mohist/pi`](pi.md) inline for one-time
tasks.

## Action Inputs

- `name` (required): static Mohist Agent name or ID. Template expressions are
  not supported.
- `prompt` (required): task input for the Agent. Template expressions are
  supported.
- `session` (optional): logical Session name within the WorkflowRun. The
  current Work ID is used when omitted.
- `timeout` (optional): deadline for this execution. Defaults to the backend
  Action's deadline.

The Agent configuration selects the execution backend, model, optional
Reasoning Effort, true model variant, and Skills. Reasoning Effort is one of
`off`, `minimal`, `low`, `medium`, `high`, `xhigh`, or `max`; it is independent
from Variant. OpenCode does not support an explicit Reasoning Effort, so choose
Pi or leave the effort unset for OpenCode. The task cannot override these
values. `prompt` supplies only the goal for this work and cannot modify the
Agent definition. Task-level constructs such as `expect`, `artifacts`,
`setVars`, and recovery behave as they do for other Actions.

`name` uses the same resolution order as the `mo` command surface. A reference
that starts with `agent_` resolves only as an ID. Other references resolve by
name first and fall back to ID when no name matches.

## Resolution and Snapshot

- Each dispatch resolves `name` to a snapshot of the current definition. The
  instructions, execution backend, model, Reasoning Effort, true model
  variant, and ordered Skills remain fixed for that attempt.
- Editing the Agent does not affect an attempt that was already dispatched. A
  retry resolves the definition again, so a repaired definition takes effect
  immediately on retry.
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

The Action defines one business error code: `agent_not_found` when `name` does
not exist at dispatch time, or the Agent is archived.

Execution errors such as backend unavailability and timeout are the same as for
the selected backend Action. Recovery `when` matching applies in the same way.

`mohist/agent` can be used only for a task and is rejected when used for a
check. If the referenced Agent does not exist or is archived, dispatch fails
with `agent_not_found`.
