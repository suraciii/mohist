# Product Vision

Mohist is an Agent-oriented software factory. It turns the path from idea to
delivery into a production line that people can define, run, and supervise.
An Issue enters and a deliverable leaves. People intervene when judgment or
exception handling is required.

## Goal

When the production line is clear enough and Agent execution is reliable
enough, one person can deliver as much as a small team.

## How People Use Mohist

People normally stay in Slack, an IDE, or another existing workspace. Mohist
executes work, records evidence, and returns results there. The long-term goal
is to complete more than 90% of daily queries, delegations, and operations in
those existing places without requiring an Agent to run outside Mohist.

A configured Mohist Agent works directly in the Web UI or CLI and can appear in
an external interaction location through an Agent Connection. Configure its
Instructions, execution settings, and Skills once in Mohist. AgentJobs launch
work and AgentSessions preserve the continuing session.

An External Agent can use the Mohist Skill and `mo` to query state, delegate
work, and perform operations. It returns the result to its existing
conversation. It does not become a Mohist Agent resource.

Mohist Issues and Workflows form the execution layer. AgentSessions retain the
traceable execution record. The Web UI is a fallback operations and
visualization plane, not a workspace that users must adopt.

## How the Factory Operates

- **Issues carry intent:** Work enters as an Issue with requirements,
  discussion, and history. Readiness stays outside execution so incomplete
  requirements do not consume capacity. See [The Workflow](the-workflow.md).
- **Workflows define the line:** A Workflow Definition declares stages, tasks,
  checks, Approval Points, and recovery. Changing the definition changes the
  production line.
- **Agents perform work:** Workflow tasks and direct entry points use the same
  Mohist Agent launch boundary. AgentSessions retain execution evidence and can
  recover from interruption.
- **Quality has a gate:** Automated checks control stage exits. Approval Points
  stop important work until a person approves or requests changes.
- **Events keep work moving:** Event routing can trigger supervision, failure
  handling, and progress responses without making a person watch every step.
- **Exceptions reach people:** When work stops, Mohist shows where it stopped,
  what it tried, and what decision is needed. People supervise exceptions
  instead of watching every step.

## Principles

- **Agents work independently:** A Mohist Agent is configurable, startable,
  continuable, and able to read results before it has an external Connection.
- **One Agent, many entry points:** The Web UI, CLI, Slack, and automation use
  the same Agent capability. Entry points handle identity, protocol, and
  presentation without keeping another copy of the Agent definition.
- **Agent-friendly interfaces first:** A Mohist Agent has a stable invocation
  interface. An External Agent can discover and operate Mohist with a Skill and
  `mo`. Critical capabilities must not exist only in the Web UI.
- **One state arbiter:** The Server decides production-line state. A Runner
  reports execution facts.
- **Reliability before breadth:** Add no mechanism that Mohist does not need.
  Make every important step visible in the Issue record.

## What Mohist Is Not

- Mohist is not an IDE, chat tool, or collaboration workspace. Users keep their
  daily collaboration in existing interaction locations while Mohist executes
  work and records evidence.
- Mohist is not CI. CI verifies a commit. Mohist advances a complete unit of
  work from requirement to integration.

## Direction

Mohist is moving toward a factory where people leave routine execution in the
loop only when they choose to supervise it. Supervision Agents can handle
proxy Approval and failure routing, while mentions and event routing make that
help configurable and revocable. See [Agent Supervision](agent-supervision.md)
and [Agent Event Routing](event-routing.md).

The same Agent should remain useful wherever the work starts. Agents should
work independently in Mohist, join existing interaction locations with
independent identities, and let External Agents use the same domain actions.
See [Agents and AgentSessions](agent-sessions.md), [Slack](slack.md),
[Skills](skills.md), and [CLI Reference](cli-reference.md).

The factory should support work whose shape becomes clear only during
execution, larger plans, and reliable fallback supervision. Session trees,
Epics, composite Issues, the Web UI, and mobile supervision with anomaly
notifications extend that direction without moving daily collaboration into
Mohist. See [Subagents and Session Trees](subagents.md), [Planning with
Epics](epics.md), [Composite Issues and Child Issues](composite-issues.md), and
[Web UI Guide](web-ui.md).

This document describes the future product, not a delivery-status list.
