# `mohist/pi` Action

`mohist/pi` delegates one unit of work to Pi and reports the execution facts.
It is a peer of [`mohist/opencode`](opencode.md): a Workflow selects one backend
with `uses`; neither wraps the other or shares its input. When a Workflow uses
it directly, it forms an Inline Agent. The Action itself is not an Agent and
does not find or start a Mohist Agent.

See [Agents and AgentSessions](../agent-sessions.md) for the overall
relationship among Agent, AgentJob, and AgentSession.

## Basic Usage

The minimal configuration contains only a prompt:

```yaml
- id: proposal
  uses: mohist/pi
  with:
    prompt: ${{ prompts.proposal }}
```

Model options use the same binding pattern as `mohist/opencode`. Set an `agent`
object in Project, Issue, or Run Variables, then bind `session` and `options`
explicitly from the Workflow Profile:

```yaml
vars:
  agent:
    model: provider-a/model-a
    variant: variant-a

stages:
  - stage: plan
    tasks:
      - id: proposal
        uses: mohist/pi
        with:
          session: plan
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
```

The `agent` Variable follows the same merge rules as other Workflow Variables:
Issue overrides Project, and Run overrides Issue. See the
[`mohist/opencode`](opencode.md) basic usage. The same `agent` object can bind
to either backend Action. For `mohist/pi`, `model`, `reasoningEffort`, and true
`variant` are separate keys. Other keys, such as `runtime` for a Mohist Agent,
are ignored and recorded in diagnostics. They do not fail execution.

When `${{ vars.agent }}` occupies the entire `options` value, its expansion is
still an object. Without an explicit `options` binding, the Action uses the
current Pi Session's model selection, or the Pi default for the first
execution.

## Action Inputs

- `prompt` is required and contains the prompt for this execution.
- `session` is optional. It names the logical Session within the WorkflowRun.
  When omitted, the current Work ID is used.
- `options` is optional. Its `model` field uses `provider/model` form, and its
  optional `reasoningEffort` field selects a canonical effort that Pi maps to
  its private thinking level. The optional `variant` field selects a true model
  variant and does not control the thinking level.
- `timeout` is optional and defaults to `3600000` milliseconds. Reaching the
  deadline interrupts the current execution.

Tools, Skills, system prompts, and automatic compaction remain Pi
configuration. Mohist does not duplicate them as fields. Action Input does not
need `agent`, `kind`, or `type`; `uses` already selects the execution backend.

The expanded Action Input is the only configuration fact for this execution.
`mohist/pi` does not read `vars.agent` implicitly.

## Workflow Session

Logical `session` names, physical Session reuse invariants, missing-Session
recovery, cleanup execution, and concurrency follow the rules in
[Action Contracts](README.md#shared-semantics-for-agent-execution-actions).
The physical Session for this Action is Pi's session file. Automatic recovery
requires that file to be explicitly missing. A corrupt or unreadable file is
not considered missing.

Session usage records the usage and cost facts that Pi provides. Redelivery
does not count the same usage event again.

## Pi Session Operations

Follow-up, Compact, and Reset follow the shared behavior and recovery rules in
[Action Contracts](README.md#shared-semantics-for-agent-execution-actions).
They operate on the currently bound Pi Session.

## Completion and Failure

See [Action Contracts](README.md#shared-semantics-for-agent-execution-actions)
for completion, promise Action Output, deadlines, exhausted provider quota, and
interruption confirmation.

Unattended Pi execution does not block for tool confirmation. Pi does not ask
for an Approval before each tool execution, and configured operations run
directly.

## Pi Responsibility Boundary

Pi ships with Mohist Runner and its version is pinned by Mohist. The installer
does not install or upgrade Pi separately. This differs from
`mohist/opencode`, whose OpenCode CLI is supplied by the installer.

The installer configures provider credentials in the Runner environment,
through environment variables or Pi's own login credentials. Mohist does not
manage API keys or collect credentials in its UI. Pi determines model
availability and the default model from configured credentials. The model list
displayed by Mohist only helps with configuration.

Pi does not ask for an Approval for every tool invocation and provides no
sandbox. Tools run with the permissions of the Runner process. For
deterministic unattended execution, Mohist does not load project-level Pi
configuration from the work repository, including settings, extensions, and
Skills under `.pi/`. A repository therefore cannot change Runner behavior by
including Pi configuration. Root-level `AGENTS.md` and `CLAUDE.md` files are
not Pi configuration; they are still provided to the model as context, as they
are for OpenCode. Customize Pi behavior in the Runner user's global Pi
configuration.

Pi controls timeout and retry behavior for individual tools. Mohist controls
only the deadline for the entire execution and interruption confirmation. It
does not add a separate timeout policy for each tool.

## Error Codes

`mohist/pi` uses only the six shared business error codes in
[Action Contracts](README.md#shared-semantics-for-agent-execution-actions). It
has no Pi-specific business error codes.
