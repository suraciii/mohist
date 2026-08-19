# Mohist Documentation

This documentation is for **users**. It provides a reading path organized by
product area. Architecture and domain analysis are under
[`../design/`](../design/).

If you are new to Mohist, read the [repository README](../README.md) first.

## Part 1: Start

- [Product Vision](vision.md): Where Mohist is going and how independent Agents
  work with external interaction locations
- [Getting Started](getting-started.md): Start from zero and move one Issue
  through the complete Workflow with a Mohist Agent, External Agent, or `mo`
- [Core Concepts](concepts.md): Understand the Mohist production-line model
- [Agents and AgentSessions](agent-sessions.md): Configure and use a Mohist
  Agent directly, and understand the work and session relationship

## Part 2: Workflows

- [The Workflow](the-workflow.md): What happens in Draft, Plan, Build, Check,
  and Integrate
- [Workflow Profile](workflow-profiles.md): Configure stages, tasks, checks, and
  Approval policy
- [Workflow Definition Reference](workflow-definition.md): The complete syntax
  for stages, tasks, expectations, recovery, and template expressions

## Part 3: Work Management

- [Repositories](repositories.md): Declare multiple repositories as Project
  execution resources and route each Issue to its target repository
- [Workspace](workspaces.md): Use persistent execution environments across
  sessions and Agents, with clean Issue initialization and persistent reuse for
  a Slack channel
- [Issue Management](issues.md): Create, start, approve, recover, and close
  Issues
- [Composite Issues and Sub-issues](sub-issues.md): Track a cross-repository
  requirement in one Issue and move its sub-issues through separate Workflows
- [Planning with Epics](epics.md): Organize separate Issues into a product goal
  that can advance automatically

## Part 4: Observation and Operations

- [Web UI Guide](web-ui.md): The board, details, evidence, and settings in the
  fallback operations and visualization plane
- [CLI Reference](cli-reference.md): The `mo` command language, command map, and
  interaction contract shared by External Agents and people
- [Observability](observability.md): Detect runtime anomalies safely and retain
  enough information for diagnosis

## Part 5: Execution Backends and Extensions

- [Action Contracts](actions/README.md): Workflow Action inputs, outputs, and
  behavior, including `mohist/opencode` and `mohist/pi`
- [External Agent API](agent-api.md): Call the shipped private API to delegate
  Agent work, recover keyed writes, read public state, and resume Session events
- [Runner Guide](runner.md): Run the execution plane and configure concurrency
- [Skills](skills.md): Give reusable capabilities to Mohist Agents and External
  Agents
- [Slack](slack.md): Use the Mohist App to manage connections conversationally,
  and use each Agent App as an independent bot in direct messages and channels
- [GitHub](github.md): Use GitHub as a requirement entry point, progress board,
  and Approval source through labels, reviews, and progress updates
- [Agent Event Routing](event-routing.md): Subscribe to events from any entity
  with a Project routing expression, then trigger Mohist Agent responses in
  order
- [Agent Supervision](agent-supervision.md): Install a supervision Agent with
  one command. It approves work and repairs failures for you until it stops and
  asks you to act.
- [Subagents and Session Trees](subagents.md): Let an Agent decompose work in its
  session through child-session spawn, terminal reports, cascading stop, and
  scheduled input

## Part 6: Deployment and Operations

- [Self-hosting](self-host.md): Run Mohist continuously on a NAS, home server,
  or laptop
- [Authentication and Access](auth.md): One Administrator plus machine
  Principals, with local zero-login access, CLI device authorization, script
  tokens, Runner registration, and Agent attribution
- [Hermes Notifications](hermes-notifications.md): Push approval points,
  failures, and completion to your chat tool
- [Troubleshooting](troubleshooting.md): Handle failures, blocked state, and
  drift

## Writing Contract

Read and follow [_agents.md](_agents.md) before you edit `docs/`.

Open an Issue when you find an outdated statement.
