# Product Vision

Mohist is an Agent-oriented software factory. It turns the path from idea to
delivery into a production line that you can define, run, and supervise. An
Issue enters and a deliverable leaves. People appear only for exceptions.

## Goal

When the production line is clear enough and Agent execution is reliable
enough, one person can deliver as much as a small team.

## How People Use Mohist

Users usually stay in Slack, an IDE, or another existing workspace instead of
entering Mohist to work. The product goal is to complete more than 90% of daily
queries, delegations, and operations in those external places. External
interaction does not require the Agent to run outside Mohist.

Mohist supports two complementary paths:

- A configured **Mohist Agent** is available directly in the Web UI or CLI. An
  Agent Connection can also expose it in Slack or another external place. Every
  entry point invokes the same Agent. Configure its Instructions, execution
  settings, and Skills once in Mohist. An AgentJob owns the first launch, and
  an AgentSession records the session.
- An **External Agent** that already runs in an IDE, Slack, or another tool can
  use the Mohist Skill and `mo` to query state, delegate work, and perform
  operations. It then returns the result to its existing conversation. It does
  not become a Mohist Agent.

Mohist Issues and Workflows form the execution layer. AgentSessions retain its
traceable execution sessions. These objects are not a new workspace into which
users must move. The Web UI is a fallback operations and visualization plane.
Use it to configure and test Agents directly, understand global and complex
state, perform critical operations when an external entry point is unavailable,
and take over manually.

## How the Factory Operates

- **The production line is code**: A Workflow Definition uses YAML to define
  stages, tasks, checks, approval points, and failure recovery. Change the
  definition to change the line. The system does not change.
- **An Issue is work in progress**: All work enters the line as an Issue. It
  carries requirements, discussion, and history from Draft to Done. Readiness
  stays outside execution so incomplete requirements do not consume capacity.
  [The Workflow](the-workflow.md) defines the default line.
- **Agents are workers**: An Inline Agent executes a Workflow task directly. A
  predefined Mohist Agent can start from the Web UI, CLI, an Agent Connection,
  an event, or a comment mention. An AgentSession records the session
  persistently so that it can recover from interruption. An External Agent can
  delegate work to the production line through a Skill, but it is not a Mohist
  Agent resource.
- **Checks and approval points provide quality control**: Automated checks
  control each stage exit. A key stage stops at an approval point and continues
  only after Approval.
- **Event routing keeps the line moving**: Every entity on the line produces
  events. A routing table subscribes to events and triggers Agent responses,
  such as proxy Approval, failure handling, and progress summaries. This lets a
  person leave the loop.
- **Escalation is the human entry point**: When an Agent stops, a notification
  and Issue comment state where it stopped and what it tried. People handle
  exceptions instead of watching the production line.

## Principles

- **The Agent works independently**: A Mohist Agent must be configurable,
  startable, able to continue a conversation, and able to read results before
  it has a Slack or other Agent Connection.
- **One Agent, multiple entry points**: The Web UI, CLI, Slack, and automation
  invoke the same Agent capability. An entry point handles only identity,
  protocol, and presentation. It does not keep another copy of Instructions,
  models, or Skills.
- **Agent-friendly interfaces first**: A Mohist Agent has a stable invocation
  interface. An External Agent can use a Skill and `mo` to discover, query, and
  operate Mohist. Critical capabilities must not exist only in the Web UI.
- **One arbiter for state**: The server decides production-line state. A Runner
  reports only execution facts.
- **Reliability before breadth**: Do not add a mechanism when Mohist can work
  without it. Make every step visible on the Issue record.

## What Mohist Is Not

- Mohist is not an IDE, chat tool, or collaboration workspace. It can hold an
  AgentSession, but it does not require users to move daily collaboration into
  Mohist. Users stay in existing interaction locations while Mohist executes
  work and records evidence.
- Mohist is not CI. CI verifies one commit. Mohist advances a complete unit of
  work from requirement to integration.

## Direction

Each linked product spec defines one direction in full:

- **People leave the loop**: Supervision Agents perform proxy Approval and
  failure handling. Issue following and comment mentions make delegation
  configurable and revocable. See
  [Agent Supervision](agent-supervision.md) and
  [Agent Event Routing](event-routing.md).
- **Independently usable Mohist Agents**: An Agent keeps the same identity,
  configuration, work model, and session model in the Web UI, CLI, and external
  connections. See [Agents and AgentSessions](agent-sessions.md).
- **Rich Agents and session trees**: An Agent spawns child sessions within its
  own session to decompose work whose shape becomes clear only at runtime.
  Mohist holds the tree, messages, and lifecycle, while the Agent plans the
  work. See [Subagents and Session Trees](subagents.md).
- **Agents in existing interaction locations**: A configured Mohist Agent can
  join Slack with an independent identity. Slack provides only the interaction
  adapter. See [Slack](slack.md).
- **External Agent support**: Third-party Agents use a stable Skill and command
  surface to operate Mohist. People and Agents use the same domain actions. See
  [Skills](skills.md) and [CLI Reference](cli-reference.md).
- **Fallback operations and visualization**: The Web UI summarizes global
  state, shows execution evidence, and supports manual operations and takeover
  when necessary. See [Web UI Guide](web-ui.md).
- **Larger production plans**: Epics advance automatically, and composite
  Issues deliver across repositories. See [Planning with Epics](epics.md) and
  [Composite Issues and Sub-issues](sub-issues.md).
- **Mobile supervision**: View production-line state and receive anomaly
  notifications on a phone. See
  [Mobile PWA and Push](../design/decisions/mobile-pwa.md) and
  [Hermes Notifications](hermes-notifications.md).

---

This document describes the future product, not a delivery-status list.
