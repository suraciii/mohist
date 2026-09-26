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

## Product Capability Areas

People organize work around outcomes, not internal resources. These areas
describe what Mohist helps them accomplish; they are not a menu or a second
domain model. [Domain Analysis](../design/domain-analysis.md) assigns business
rules to their owners. Each area links to representative features, not a
delivery-status inventory.

### Work Organization

Express a goal, divide the work, decide its order, and see what was delivered.
This area covers requirements, decomposition, prerequisites, and goal-level
progress; it does not decide how an execution runs or recovers. Its primary
subdomain is Issue, including Epic organization. Start with
[Issues](issues.md), [Composite Issues](composite-issues.md), and
[Epics](epics.md).

### Delivery Workflows

Define how work reaches an accepted deliverable, then advance or recover a
particular Run. Workflow owns the stages, checks, Approval Points, and recovery
rules. It uses Agent execution and Workspace resources without owning their
lifecycles. Start with [The Workflow](the-workflow.md) and
[Workflow Profiles](workflow-profiles.md).

### Agent Collaboration

Configure an Agent, delegate a task, continue the conversation, and receive its
result. Direct work does not require creating an Issue. Agent owns reusable
execution capability and Connections; Session owns the continuing conversation
and its inputs, Turns, and evidence. Start with
[Agents and AgentSessions](agent-sessions.md), [Subagents](subagents.md), and
[Slack interaction](slack.md).

### Operations and Supervision

Understand what is happening, identify work that needs attention, and take an
authorized next action. This area combines facts from Workflow, Session,
Runner, and other owners; it does not introduce another state authority.
AgentOps assembles read-side views, while controls act through the domain that
owns the operation. Start with
[Activity](../specs/agent-ops/activity/spec.md),
[Agent Supervision](agent-supervision.md), and
[Observability](observability.md).

Project and Repository configuration, Workspace, Runner, and access controls
support these areas. They remain explicit resources without each becoming a
top-level user task. Slack, CLI, Web, and API expose the same capabilities
through different interaction locations.

## End-to-End Paths

The areas work together in two common paths:

1. **Software delivery:** organize a requirement as an Issue, plan and approve
   the work, build and check it, then integrate the verified deliverable.
   Deployment belongs to the path only when the Workflow defines it; a merge
   alone does not establish a deployed result.
2. **Direct delegation:** select an Agent, launch a task, continue or stop its
   Session as needed, and return the result to the originating context. A
   recorded delivery attempt alone does not establish that the caller received
   the result.

Both paths use the same execution resources and supervision capabilities.
When an outcome is unknown or work fails, expose the evidence and the safe
recovery or human decision needed to continue. Keep execution and delivery
outcomes distinct rather than reporting uncertainty as success.

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
- **Results are traceable to a cause:** Each result points to its stage,
  change, or check, with evidence. An Agent can find why a step failed
  without reading full logs.
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
- **Issues carry complete objectives:** An Issue contains everything an Agent
  needs to finish the work: requirements, acceptance criteria, and boundaries.
  The Agent must not have to ask for decisions after the work starts.
- **Feedback density over volume:** Each result is local to a change or a
  stage, quick to get, and objectively verifiable. An Agent uses the cheapest
  check that can answer its current question. Feedback that is slow or does
  not identify a cause is a product defect.
- **Each run reduces the cost of the next:** Verified lessons go back into
  Skills, repository context, and automated checks. A defect that gets past
  the checks becomes a new check. The next task starts with what the last
  task learned.
- **Delegation follows three properties:** Work can run unattended when its
  objective is complete, its feedback shows causes, and its lessons return to
  the system. Mohist improves these three properties so more work can be
  delegated safely. People keep objectives, boundaries, and risk judgment.
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

Parallel work depends on structure more than on scale. Workers need
non-overlapping groups, written assignments, isolated work areas, interfaces
that stay unchanged, and a commit after each verified step. Clear, verifiable
bulk work, such as cleanup, migration, and hardening, is rarely scheduled
by hand. This is the first class of work that parallel delegation makes
economical.

This document describes the future product, not a delivery-status list.
