# `mohist/opencode` Action

`mohist/opencode` delegates one unit of work to OpenCode and reports the
execution facts. When a Workflow uses it directly, it forms an Inline Agent.
The Action itself is not an Agent and does not find or start a Mohist Agent.

See [Agents and AgentSessions](../agent-sessions.md) for the overall
relationship among Agent, AgentJob, and AgentSession.

## Basic Usage

The minimal configuration contains only a prompt:

```yaml
- id: proposal
  uses: mohist/opencode
  with:
    prompt: ${{ prompts.proposal }}
```

To let a Project or Issue adjust OpenCode configuration, first set it in
separate Variables:

```yaml
vars:
  agent:
    model: provider-a/model-a
    variant: variant-a
```

Then bind `session` and `options` explicitly from the Workflow Profile:

```yaml
stages:
  - stage: plan
    tasks:
      - id: proposal
        uses: mohist/opencode
        with:
          session: plan
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
```

When `${{ vars.agent }}` occupies the entire `options` value, its expansion is
still an object. The same object can be stored in Project, Issue, or Run
Variables, or inlined in the task. Issue values override Project values, and
Run values override Issue values. A Workflow Profile references Variables but
does not store their values.

`agent` is an existing Workflow Variable name. In this Action, it supplies
`model` and true `variant` values. An explicit `reasoningEffort` is rejected as
`unsupported_execution_configuration`; it does not identify a Mohist Agent or
select an OpenCode agent.

The expanded Action Input is the only configuration fact for this execution.
`mohist/opencode` does not read `vars.agent` implicitly. Without an explicit
`options` binding, it uses the selection from the current OpenCode Session, or
the OpenCode default for the first execution.

## Action Inputs

- `prompt` is required and contains the prompt for this execution.
- `session` is optional. It names the logical Session within the WorkflowRun.
  When omitted, the current Work ID is used.
- `options` is optional. Its `model` field uses `provider/model` form, and its
  optional `variant` field selects a true variant supported by that model. An
  explicit `reasoningEffort` is rejected as
  `unsupported_execution_configuration`.
- `timeout` is optional and defaults to `3600000` milliseconds. Reaching the
  deadline interrupts the current execution.

Tools, plugins, permissions, default execution behavior, and automatic
compaction remain OpenCode configuration. Mohist does not duplicate them as
fields. Action Input does not need `agent`, `kind`, or `type`; `uses` already
selects the execution backend.

## Workflow Session

Logical `session` names, physical Session reuse invariants, missing-Session
recovery, cleanup execution, and concurrency follow the rules in
[Action Contracts](README.md#shared-semantics-for-agent-execution-actions).
The physical Session for this Action is an OpenCode Session. Automatic recovery
requires OpenCode to report explicitly that the Session does not exist.

## OpenCode Session Operations

Follow-up, Compact, and Reset follow the shared behavior and recovery rules in
[Action Contracts](README.md#shared-semantics-for-agent-execution-actions).
They operate on the currently bound OpenCode Session.

## Completion and Failure

See [Action Contracts](README.md#shared-semantics-for-agent-execution-actions)
for completion, promise Action Output, deadlines, exhausted provider quota, and
interruption confirmation.

OpenCode permission configuration remains authoritative. Allowed operations run
directly, and explicitly denied operations remain denied. If OpenCode only asks
for confirmation, Mohist's unattended execution permits that operation once.
It does not persist the permission, create an Approval, or require user
intervention. If the response cannot complete the operation, the task fails
immediately with an actionable error instead of waiting for its deadline.

## OpenCode Responsibility Boundary

The installer provides a working OpenCode CLI and configures providers,
plugins, and permissions. Mohist does not install, upgrade, or pin the exact
OpenCode CLI version. At startup, Mohist validates the current environment and
prevents the Runner from accepting new work when it is incompatible.

OpenCode controls timeout and retry behavior for individual tools. Mohist
controls only the deadline for the entire execution and interruption
confirmation. It does not add a separate timeout policy for each tool.

The model list displayed by Mohist helps with configuration. OpenCode remains
authoritative for model validity and the default model.

## Error Codes

See [Action Contracts](README.md#shared-semantics-for-agent-execution-actions)
for the six shared business error codes and platform errors.
`mohist/opencode` also defines:

- `incompatible-runtime` means the OpenCode version or data is incompatible
  with Mohist.
- `permission-required` means permission is required to continue.
- `interrupted` means a signal outside the Runner interrupted execution.
