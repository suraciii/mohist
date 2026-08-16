# `mohist/agent` Action

`mohist/agent` delegates one Workflow task to a configured Mohist Agent. The
Workflow resolves the Agent once, freezes its execution definition, and
activates a real AgentJob with a minted AgentSession, first SessionInput, and
first AgentTurn. The AgentJob owns Agent execution and its terminal result;
the Workflow owns task completion, `expect`, `artifacts`, `setVars`, recovery,
and advancement.

This is a **BREAKING** ownership change for consumers that previously treated
`mohist/agent` as an inline TaskRun action. The stable contract is the
`workflowInvocation` projection on the Workflow task read surface:
`status` is one of `queued`, `executing`, `completed`, `failed`, `cancelled`, or
`recovering`, and the projection includes `invocationId`, `jobId`, `sessionId`,
`inputId`, and `turnId`. The terminal `result` is read from AgentJob terminal
facts. It does not require reading or parsing the AgentSession transcript.

## Basic Usage

```yaml
- id: review
  uses: mohist/agent
  with:
    name: reviewer
    prompt: ${{ prompts.review }}
```

The Agent selected by `name` provides identity instructions, execution backend
(OpenCode or Pi), model, variant, and Skills. `prompt` is the input for this
task. Use this Action when a Workflow task needs a configured Agent. Continue
to use [`mohist/opencode`](opencode.md) or [`mohist/pi`](pi.md) for an inline,
one-time task that should remain TaskRun-owned.

## Action Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `name` | Yes | - | Static Mohist Agent name or ID. Template expressions are not supported. |
| `prompt` | Yes | - | Task input for the Agent. Template expressions are supported. |
| `session` | No | - | Logical Session label for this Workflow attempt. The Work ID is used when omitted. |
| `timeout` | No | Same as the backend Action | Per-invocation deadline for the AgentJob. |

The Agent configuration selects the execution backend, model, variant, and
Skills. The task cannot override them. `prompt` supplies only the goal for this
work and cannot modify the Agent definition. Task-level constructs such as
`expect`, `artifacts`, `setVars`, and recovery are applied by the Workflow
finalizer after the AgentJob terminal result arrives.

`name` uses the same resolution order as the `mo` command surface. A reference
that starts with `agent_` resolves only as an ID. Other references resolve by
name first and fall back to ID when no name matches.

## Resolution, Freeze, and Lineage

- Each attempt resolves `name` during durable handoff preflight. The
  instructions, execution backend, model, variant, ordered Skills, workspace,
  timeout, and task completion contract are frozen before acceptance.
- Editing the Agent after acceptance does not change that invocation. A retry
  is a new attempt and resolves the current definition again.
- Acceptance is not execution. The handoff may be accepted while the AgentJob
  waits for Agent readiness, a concurrency permit, or an eligible Runner. Such
  waiting is exposed as `queued`; it does not independently fail the Workflow
  task.
- The Workflow task projection and the AgentJob/Session projections carry the
  same minted identifiers. Session labels resolve the owning WorkflowRun and
  TaskRun without using transcript content.
- A `failed` AgentJob remains the Agent execution verdict. If the Workflow
  finalizer has a recovery decision pending or applying, the invocation status
  is `recovering` until that durable decision is settled.

The `session` value is a logical label, not a cross-attempt reuse key for this
path. Every Workflow Agent attempt mints its own AgentSession, SessionInput,
and AgentTurn. Two attempts with the same `session` value therefore retain the
same human-readable label but never merge their execution identities or
conversation records. This is the resolved named-session behavior for the
BREAKING cutover.

## Failure Semantics

| Error code | Meaning |
|---|---|
| `agent_not_found` | `name` does not exist at dispatch time, or the Agent is archived. |

Execution errors such as backend unavailability and timeout are recorded on the
AgentJob and projected as the terminal result. A completed AgentJob can still
fail the Workflow task when its frozen `expect` is unsatisfied; this preserves
the distinction between Agent execution and Workflow completion policy.
Recovery `when` matching applies in the same way as other task failures.

`mohist/agent` can be used only for a task and is rejected when used for a
check. Profile save and `mo workflow validate` require only the `name` and
`prompt` input shape; they do not check whether the Agent exists. If the
referenced Agent cannot be resolved or the durable input is invalid, dispatch
fails with the existing rejection code and no unowned handoff work is sent to a
Runner.
