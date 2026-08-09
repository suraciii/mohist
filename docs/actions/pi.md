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
    model: anthropic/claude-sonnet-4
    reasoningEffort: high

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
to either backend Action. For `mohist/pi`, the valid keys are `model`,
`reasoningEffort`, and `variant`. Unknown or malformed values fail before work
begins; they are not ignored or recorded as a successful diagnostic.

When `${{ vars.agent }}` occupies the entire `options` value, its expansion is
still an object. An omitted `options` does not take a model or reasoning effort
from an existing Pi Session. A Profile that needs a specific setting binds it
explicitly. The saved-Agent default rule belongs to the separate `mohist/agent`
Action, not to an Inline Action.

## Action Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `prompt` | Yes | - | Prompt sent to Pi for this execution |
| `session` | No | - | Logical Session name within the WorkflowRun; the current Work ID is used when omitted |
| `options` | No | - | Object that selects Pi execution settings for this execution |
| `options.model` | No | - | Pi model in `provider/model` form |
| `options.reasoningEffort` | No | - | Reasoning effort: `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, or `max` |
| `options.variant` | No | - | Pi-specific variant, separate from reasoning effort |
| `timeout` | No | `3600000` | Execution deadline in milliseconds; reaching it interrupts the current execution |

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

Session usage records input, output, cache read, cache write, and thought tokens
when Pi provides them. Cache write is not included in cache read and is not
counted again when an event is redelivered.

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
manage API keys or collect credentials in its UI. Mohist validates configuration
against its versioned Pi catalog; it does not probe configured credentials for a
live model list. An exact configuration that later cannot run waits for that
same configuration instead of falling back to another model, effort, or variant.

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

`mohist/pi` uses only the shared business error codes in
[Action Contracts](README.md#shared-semantics-for-agent-execution-actions). It
has no Pi-specific business error codes.

## Implementation Gaps

Both Workflow and AgentJob paths are implemented. A Workflow can use
`mohist/pi`, and a Mohist Agent configured for Pi can execute input. Both reuse
AgentSession and show transcripts, tools, state, compaction, model, usage, and
cost on the existing Session page. Pi Compact and Reset are available through
the shared Session operations.

The closed option grammar, static execution configuration checks, and separate
Reasoning effort are target behavior until saved Agent execution configuration
is delivered.[^433] One-job CLI override, preview, and immutable readback then
build on that saved-default contract.[^434]

[^433]: Delivery gap [#433](https://github.com/suraciii/mohist/issues/433): saved execution configuration contract. It has no dependency on #434.
[^434]: Delivery gap [#434](https://github.com/suraciii/mohist/issues/434): one-job override and readback contract. It depends on #433.

Before a new Workflow input is submitted, Mohist automatically creates empty Pi
context and replaces a binding that the owning Runner confirms is missing while
the Session is safely idle. AgentJob launch and idle Follow-up do not yet use
that recovery boundary. Ambiguous or unsafe absence still blocks instead of
replaying input. The complete ownership lease, fencing, candidate reconciliation,
and cleanup contract is not yet enforced at every boundary; see [Agents and
AgentSessions](../agent-sessions.md#implementation-gaps).
