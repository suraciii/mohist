# Agents and AgentSessions

A Mohist Agent is a configurable resource in a Project. People can start it in
the Web UI or CLI, connect it to Slack, or let it respond to events and
mentions. Every entry point uses the same Agent, AgentJob, and AgentSession
model. An External Agent is separate: it uses the Mohist Skill and `mo` to
operate Mohist and is not a Mohist resource.

## Product Commitments

- Users can configure and start an Agent without Slack or another external
  connection.
- The Agent owns its identity, Instructions, Runtime, Model, Reasoning Effort,
  Variant, Skills, and concurrency limit. Entry points do not copy or override
  that definition for one request.
- The Agents surface is the only place to configure an Agent's Runtime, Model,
  Reasoning Effort, Variant, and Skills. No Project setting supplies, inherits,
  or recommends these values.
- A new delegation creates one AgentJob, AgentSession, first SessionInput, and
  first AgentTurn. A Follow-up adds an Input to an existing Session and does not
  create another AgentJob.
- AgentJob reports the first delegation result. AgentSession records the
  continuing conversation and whether it can accept more input.
- A Slack message or Web page never becomes the state authority.
- Mohist gives an Agent identity references and self-service guidance. The
  Agent can use `mo` to read Issue, Epic, Repository, and Session details when it
  needs them.

## Concept Layers

- **Mohist Agent**: A reusable Agent resource within a Project. It has a stable
  identity, Instructions, execution configuration, Skills, and state.
- **Agent Connection**: An external entry point for one Agent. It references the
  Agent and owns neither its configuration nor an AgentSession.
- **AgentJob**: One launch execution. It records waiting, execution, completion,
  failure, retry, and recovery for the first delegation.
- **SessionInput**: One input accepted by an AgentSession. It has stable identity,
  content, attachments, source, order, and delivery state.
- **AgentTurn**: One continuous Runtime processing period for ordered Inputs. It
  is owned by an AgentSession and is not top-level work.
- **AgentSession**: The continuing logical Session and audit record. It owns
  Inputs and Turns in order, context, usage, Activity, and current Runtime
  Binding. It has no completed or failed lifecycle.
- **Runtime Session**: The physical conversation maintained by OpenCode, Pi,
  Codex, or another backend. It can be replaced without changing AgentSession
  identity.

An Action is the Agent-to-Runner execution contract. It carries the accepted
Agent snapshot to a backend but has no Agent identity and owns no work lifecycle.

## One Invocation Path

Every entry point starts a real Mohist Agent through one launch boundary. This
includes Workflow, Web, CLI, Agent Connections, event routing, and comment
mentions. Each launch creates an AgentJob, and AgentJob owns execution state,
result, retry, and recovery.

Workflow names the Agent and supplies task input and attribution. It consumes the
AgentJob result to decide Stage advancement. It does not select a Runtime, copy
Agent configuration, or create anonymous Agent capability.

Built-in Profiles use built-in Mohist Agents. A Project Profile may name another
ready Agent. A missing, archived, or not-ready Agent fails launch explicitly;
Mohist does not fall back to a Runtime-specific Workflow Action.

## Launch Entry Points

A task-first launch is available when the caller has a task but does not yet
need to configure an Agent. The [External Agent API](../../interfaces/agent-api/spec.md#task-first-launch)
defines its route and replay contract. The request accepts Runtime, Model,
Reasoning Effort, and Variant hints. The Server derives missing definition
fields, creates the Agent carrying only the supplied execution values, and uses
the same AgentJob and AgentSession launch path. Unset fields stay unset and mean
Runtime behavior.

- Web and CLI task-first launches create a derived Agent and return the same
  Job, Session, Input, and Turn identities.
- An Agent Connection sends a first task through a Slack DM, explicit New task,
  or channel root mention.
- Event routing starts an AgentJob and AgentSession for a matching event.
- A comment mention uses the comment after `@<agent-name>` as task input and
  associates the Issue context.

Selecting or naming an existing Agent in the Web UI or CLI uses the same
unchanged definition-first launch path. The CLI also supports
`mo agent start --prompt <task>` for task-first launch.

A mention is one-time work. For continuous attention, the Agent adds the Issue
to its watch list with `mo issue watch add`. Every entry point fixes the Agent
snapshot for that work. A Mohist Agent may also spawn child Sessions; see
[Subagents and Session Trees](../../session/subagents/spec.md).

## Current Scope

Every entry point uses the unified AgentJob path. A Workflow task names a
Mohist Agent through `mohist/agent`; the Agent definition selects OpenCode, Pi,
or Codex and the accepted snapshot fixes that backend to the AgentJob. Max
concurrent runs applies to launches and Follow-ups. See
[Agent Event Routing](../event-routing/spec.md) for Agent responses to matching events.

## External Agent, Mohist Agent, and Agent Connection

An **External Agent** interacts with the user outside Mohist. It may run in
Slack, an IDE, or another Agent host. It uses the Mohist Skill and `mo` to
query, delegate, or operate Mohist. It is not a Mohist resource, and Mohist
does not schedule it.

A **Mohist Agent** is a reusable Agent resource in one Project. It has a stable
ID, name, Instructions, Skills, and execution configuration. A Workflow task,
the Web UI, CLI, Agent Connection, event routing, or comment mention starts it
through the same AgentJob launch boundary. Workflow tasks do not select
Runtime-specific Actions or create anonymous Agent capability.

An **Agent Connection** exposes one Mohist Agent in an external interaction
location. A Slack Agent Connection lets a Slack Bot represent a specified
Agent. Slack receives messages and presents replies. The Agent owns
understanding, execution, and the AgentSession. The Connection does not copy
Agent configuration and cannot switch to another Agent within one conversation.

Slack has two App identities:

- The **Mohist App** is the management entry point installed once in a Slack
  Workspace. It establishes the Workspace connection and manages Agent
  Connections. It is not a business Agent or an Agent App.
- An **Agent App** is an execution entry point. Each connected Agent has a
  separate Slack App and Bot identity that accepts work and returns results.

Management operations and work tasks use separate identities. One identity
does not send on behalf of the other.

An **AgentSession** records messages, context, usage, Activity, and the current
Runtime Session for a conversation. An **AgentJob** owns every top-level Agent
execution, including a `mohist/agent` Workflow task. A **WorkflowRun** owns
orchestration state, its complete bound Definition, and the AgentJob reference.
Subsequent input continues the same AgentSession but does not rewrite the
AgentJob. Each accepted input is a **SessionInput**. One continuous Runtime
processing period is an **AgentTurn**. One AgentTurn can process multiple
SessionInputs in order.

Messages, execution, and work results therefore do not share one state. See
[Session inputs and Turns](../../session/input-and-turns/spec.md) and
[Slack connections](../../integrations/slack/connections/spec.md).

## Implementation Gaps

- Not every entry point exposes acceptance, dispatch, and Turn result as
  separate resumable facts after disconnection.

- Task-first creation does not yet accept a Reasoning Effort hint.
