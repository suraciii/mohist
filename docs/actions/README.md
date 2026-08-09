# Action Contracts

An Action is an execution interface selected by a Workflow task through
`uses`. Each Action defines its own `with` inputs, outputs, and failure
semantics. It does not decide whether the Workflow is complete and does not
represent a Mohist Agent with an identity.

Each Action contract is declarative and has three parts:

- **Inputs**: Names, required status, and default values. A task's `with` value
  is validated against the declaration. Unknown fields, missing required
  fields, and invalid types are rejected when the Profile is saved instead of
  failing only at runtime. There are no hidden inputs outside the declaration.
- **Outputs**: Fields produced on success for `setVars`,
  `${{ tasks.<id>.outputs.* }}`, and recovery matching.
- **Error codes**: Stable identifiers for all business failures produced by
  the Action. Recovery matches them with `when: error.code=...`. Human-readable
  error messages are not matching contracts.

The platform can also produce `invalid-input`, `unexpected-error`, and
`timeout`. These indicate input validation failure, an unexpected platform
failure, and a missed deadline. They are not business errors owned by an
Action.

This directory contains product contracts for Actions that need separate
documentation. See [Workflow Profiles](../workflow-profiles.md) for Workflow
stages, tasks, `expect`, and recovery configuration. See
[Agents and AgentSessions](../agent-sessions.md) for the relationship among
Actions, Inline Agents, and Mohist Agents.

Write the active documentation in English. Preserve product terms,
configuration fields, and commands exactly.

## Current Actions

- [`mohist/opencode`](opencode.md): Executes one input through OpenCode and
  defines model options, Workflow Session behavior, and Session operations.
- [`mohist/pi`](pi.md): Executes one input through Pi. It is a peer of
  `mohist/opencode` and shares its model-option shape and Session semantics,
  but has different installation and trust boundaries.
- [`mohist/agent`](agent.md): Executes a task from a predefined Mohist Agent
  snapshot. It uses the same mechanism as an Inline Agent and does not create
  an AgentJob.

**Git Actions** define explicit `with` input contracts for workspace
preparation, rebase, rebase status, merge readiness, and push.

- [`mohist/workspace-prepare`](git.md#mohistworkspace-prepare)
- [`mohist/rebase`](git.md#mohistrebase)
- [`mohist/rebase-status`](git.md#mohistrebase-status)
- [`mohist/merge-ready`](git.md#mohistmerge-ready)
- [`mohist/push`](git.md#mohistpush)

**GitHub PR Actions** define explicit `with` input contracts for PR creation,
ready state, checks, status validation, and squash merge.

- [`mohist/create-github-pr`](github-pr.md#mohistcreate-github-pr)
- [`mohist/mark-github-pr-ready`](github-pr.md#mohistmark-github-pr-ready)
- [`mohist/merge-github-pr`](github-pr.md#mohistmerge-github-pr)
- [`mohist/github-pr-checks`](github-pr.md#mohistgithub-pr-checks)
- [`mohist/github-pr-status`](github-pr.md#mohistgithub-pr-status)

**Core Actions** run processes and inline scripts, and check file existence and
markers.

- [`core/process`](core.md#coreprocess)
- [`core/script`](core.md#corescript)
- [`core/artifact-exists`](core.md#coreartifact-exists)
- [`core/marker`](core.md#coremarker)

**OpenSpec Actions** load `tasks.json`, verify OpenSpec change artifacts, and
archive a change.

- [`mohist/openspec-tasks`](openspec.md#mohistopenspec-tasks)
- [`mohist/openspec-artifacts`](openspec.md#mohistopenspec-artifacts)
- [`mohist/archive-change`](openspec.md#mohistarchive-change)

Pi is an independent peer Action, not an input extension of
`mohist/opencode`.

## Shared Semantics for Agent Execution Actions

`mohist/opencode` and `mohist/pi` share the following semantics. Their own
pages describe only their differences. `mohist/agent` resolves an Agent
definition into the same kind of execution and follows these semantics too.

### Execution Options

For `mohist/opencode` and `mohist/pi`, `options` accepts only `model`,
`reasoningEffort`, and `variant`. `reasoningEffort` is one of `none`, `minimal`,
`low`, `medium`, `high`, `xhigh`, or `max`. `variant` is a separate
backend-specific setting, never another name for reasoning effort.

Every supplied value is checked against Mohist's versioned runtime catalog before
work begins. Unknown or malformed options return
`invalid_execution_configuration`; a Runtime or model outside that catalog
returns `unsupported_execution_configuration`; and an effort or Variant that
cannot be used with the selected model returns
`incompatible_execution_configuration`. If Mohist cannot read the required
catalog, it returns `execution_catalog_unavailable`. Mohist does not ignore a
setting, query a provider, or substitute a different model, effort, or Variant.

### Workflow Session

`session` identifies a logical AgentSession whose origin is a Workflow. Tasks
with the same name in one WorkflowRun share conversation context. Different
names are isolated. When `session` is omitted, Mohist uses the Work ID so two
tasks do not accidentally share a conversation. Changing the execution backend
preserves the logical identity but starts an empty physical Session. Mohist does
not migrate the old conversation or create physical Session history.

### Physical Session Reuse Invariants

When tasks in one WorkflowRun specify the same `session` name, Mohist must keep
using the physical Session currently bound to that AgentSession. A different
task, task retry, or change to `options.model`, `options.reasoningEffort`, or `options.variant` cannot
replace it. Model selection affects only the current execution and takes effect
in the existing Session.

| Change | Physical Session |
|---|---|
| A later task or retry uses the same `session` name | Unchanged |
| `options.model`, `options.reasoningEffort`, or `options.variant` changes | Unchanged |
| Compact | Unchanged |
| Reset | Creates a new empty Session; the AgentSession keeps its conversation content |
| The current Session is confirmed missing before a new independent input is submitted | Creates a new empty Session automatically |
| Working directory changes | Rejects execution; use a new logical `session` name |
| Execution backend changes | Creates a new empty physical Session |

Automatic recovery applies only when the responsible Runner still owns the
current binding, the backend explicitly confirms that the old Session is
missing, and the current input has not been accepted. Mohist fails explicitly
when the request reaches another Runner, the backend is temporarily
unavailable, the response is ambiguous, or the prompt might already have been
submitted. It does not replace the binding or replay the prompt. The new
Session has no old context. The same AgentSession continues to show existing
messages and indicates that later work starts with reset context.

When a task has completed its work but still has changes to commit or restore,
Mohist continues the cleanup execution in the same AgentSession and physical
Session. Cleanup does not replace the Session and does not require Reset first.

Only one Workflow-originated input runs in an AgentSession at a time. Different
AgentSessions can run concurrently. A follow-up submitted from the Session page
is the exception: it joins the current execution when the Session is running
and starts a new execution when the Session is idle.

### Session Operations

| Operation | Result |
|---|---|
| Follow-up | Sends user text to the current physical Session and returns after the backend accepts it |
| Compact | Uses native backend compaction; the Runtime Session identity remains unchanged |
| Reset | Creates an empty physical Session while idle; the AgentSession keeps its conversation content |

Compact is a user operation within a Session, not a Workflow Action. Mohist
does not simulate compaction with a synthetic summary and does not silently
degrade when compaction fails. After a Runner restart, these operations still
use the binding stored by the AgentSession. Compact and operations against a
running execution do not recover a missing Session automatically. Reset can
create an empty Session even when the old Session is already missing.

### Completion and Failure

After execution succeeds, the Workflow uses the task's `expect`, `artifacts`,
`failIf`, and recovery rules to decide what happens next. When execution fails,
is cancelled, or times out, that original error is the task result. Mohist does
not then inspect files or markers. Action Output is `{ "promise": "..." }`
only when a promise marker matches; otherwise it is `null`. Session ID, model,
usage, full text, and validation details belong to Session or task state and
are not placed in Action Output.

The execution deadline starts before Mohist submits the prompt. Preparing the
binding and audit input does not consume this budget. A cleanup prompt is a new
execution with a new deadline. When the deadline expires, Mohist interrupts the
current execution and reports `timeout`. Backend interruption is cleanup and
cannot replace `timeout` with a missing-marker result. It also cannot replace
the current Session binding or perform an automatic Reset. Mohist does not
replay a prompt when submission is uncertain because that could execute one
task twice.

When the provider explicitly reports exhausted quota, balance, or billing,
Mohist interrupts the current execution and fails the task without waiting for
provider retries. The Session binding remains unchanged. After the Session
becomes idle, work can continue with another model without Reset. If Mohist
cannot confirm that execution stopped, it reports that interruption is
unconfirmed instead of presenting a possibly running Session as safely idle.

### Shared Error Codes

The two execution Actions share these business error codes. Their own pages
list only additional codes.

| Error code | Meaning |
|---|---|
| `invalid_execution_configuration` | An execution option is unknown, malformed, empty, or uses an invalid reasoning effort |
| `unsupported_execution_configuration` | A well-formed Runtime or model is not in the Runtime catalog |
| `incompatible_execution_configuration` | The selected model cannot use the requested reasoning effort or Variant |
| `execution_catalog_unavailable` | Mohist cannot read the static configuration catalog needed to validate this Action |
| `runtime-unavailable` | Backend execution capability is not ready or available |
| `session-workspace-mismatch` | The working directory does not match the Session binding |
| `session-binding-failed` | Logical Session binding resolution or persistence failed |
| `runtime-session-missing` | The physical Session is missing, but this operation cannot rebuild or resubmit safely |
| `unavailable-runtime` | The backend reports that it is unavailable |
| `execution-failed` | Execution failed, including exhausted provider quota, balance, or billing |

## Implementation Status

The three Actions and their existing Session behavior are implemented. The
closed execution-option grammar, static catalog checks, and distinct Reasoning
effort semantics above are target behavior until saved Agent execution
configuration is delivered.[^433] One-job CLI override, preview, and immutable
readback build on that saved-default contract.[^434]

[^433]: Delivery gap [#433](https://github.com/suraciii/mohist/issues/433): saved execution configuration contract. It has no dependency on #434.
[^434]: Delivery gap [#434](https://github.com/suraciii/mohist/issues/434): one-job override and readback contract. It depends on #433.
