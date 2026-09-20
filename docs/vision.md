# Product Vision

Mohist is an Agent-oriented software factory. It turns the path from idea to
delivery into a production line that people can define, run, and supervise.
An Issue enters and a deliverable leaves. People retain direction and judgment
throughout delivery, while Agents perform delegated work.

## Goal

When the production line is clear enough and Agent execution is reliable
enough, one person can deliver as much as a small team while retaining the
ability to understand, evaluate, and redirect the work.

## Philosophy

Software projects need sustained human judgment to remain useful and
maintainable. People set direction, judge value, and decide which complexity
is worth keeping. AI can perform and review work, but agreement among Agents
and passing checks cannot establish that the project is moving in the right
direction.

Mohist amplifies human judgment and attention. It takes on routine execution,
coordination, and evidence gathering so people can spend their attention on
goals, trade-offs, and actual results. Human involvement remains part of the
operating model as automation improves.

Effective supervision requires more than an approval button. People must be
able to understand what changed, inspect the evidence, question the
assumptions, and redirect the work. Mohist succeeds when one person can guide
more work while retaining that understanding and control.

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
- **Results are traceable to a cause:** Each result points to its stage,
  change, or check, with evidence. An Agent can find why a step failed
  without reading full logs.
- **Events keep work moving:** Event routing can trigger supervision, failure
  handling, and progress responses without making a person watch every step.
- **Exceptions reach people:** When work stops, Mohist shows where it stopped,
  what it tried, and what decision is needed. People supervise exceptions
  instead of watching every step.

## Product Principles

- **Agents work independently:** A Mohist Agent is configurable, startable,
  continuable, and able to read results before it has an external Connection.
- **One Agent, many entry points:** The Web UI, CLI, Slack, and automation use
  the same Agent capability. Entry points handle identity, protocol, and
  presentation without keeping another copy of the Agent definition.
- **Agent-friendly interfaces first:** A Mohist Agent has a stable invocation
  interface. An External Agent can discover and operate Mohist with a Skill and
  `mo`. Critical capabilities must not exist only in the Web UI.
- **Issues carry complete objectives:** An Issue contains the requirements,
  acceptance criteria, and boundaries needed to start work. When new evidence
  requires a decision outside those boundaries, the Agent must bring that
  decision to the user. An Agent proposal does not become a user decision
  without the user's approval.
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

Mohist is moving toward a factory that reduces routine execution and
coordination work so people can focus on direction, trade-offs, and results.
People can inspect and redirect work even when no failure has been reported.
Supervision Agents can handle proxy Approval and failure routing within their
delegated authority, while mentions and event routing make that help
configurable and revocable. See [Agent Supervision](agent-supervision.md)
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
