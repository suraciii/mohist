# Agent Event Routing

In this document, Agent means a **Mohist Agent** with a stable ID, name, and
Instructions. It does not mean an Inline Agent invoked directly by a Workflow.
See [Agents and AgentSessions](agent-sessions.md) for their relationship.

## Problem

Workflow, Issue, Epic, Runner, Workspace, and AgentSession all produce events.
A stage waits for an Approval, a task fails, an Issue completes, an Epic has no
Issue that can advance, or a Runner disconnects.

Workspace lifecycle publishes `com.mohist.workspace.created` and
`com.mohist.workspace.archived`; the payload carries the Workspace identity,
and the source records what created it (issue, manual, slack, web, or cli).

Event routing delegates responses to Agents. A Project maintains an ordered
**routing table**. Each rule declares the event it matches, the Agent that
responds, and the response prompt. When an event occurs, Mohist evaluates the
table. A matching Agent starts automatically, reads context under the prompt,
and performs actions.

Each match creates an AgentJob, an AgentSession, the first SessionInput, and the
first AgentTurn. AgentJob records whether the response completed. AgentSession
records input, execution turns, conversation, and tool calls.

Here an Agent is a proxy. It occupies a position that a human owner could hold
in the production system. An owner can decide an Approval, analyze a failure,
write a summary, or create a follow-up Issue; an Agent uses the same actions.
It is not a privileged channel. The prompt contains decision logic. Mohist
matches events, starts the Agent, and records the response.

## Three Mental Models

### 1. Events Carry Business Lineage

Every event has **attributes** that locate it in the production system. They
include event type (`event.type`), Issue (`event.issue`), Epic, WorkflowRun, and
stage. Any event around an Issue carries that Issue number whether the event
came from Workflow or Issue itself.

Watching everything under Issue #42 therefore needs only one expression.

### 2. Two Prompt Layers

An Agent's effective instruction has two layers:

- **Agent Instructions define identity.** Configure them once. They remain
  stable and are shared by every rule. For example: "You are the owner's proxy.
  Decide Plan and Check Approvals carefully, and escalate uncertainty to the
  owner."
- **A rule response prompt defines this reaction.** Configure it on each rule.

One Agent can respond to several event types. Putting every response into its
identity would make the Agent definition large and hard to reuse. Identity
belongs to the Agent; reaction belongs to the rule.

### 3. Evaluate the Routing Table in Order

Rules form an **ordered table**, like mail filters. Mohist evaluates each rule
from top to bottom. On a match, it triggers that rule's Agent and stops by
default. Mark an earlier rule as `continue` when several Agents should respond
to the same event.

This model expresses three needs:

- **Exclusive response:** First match naturally selects one decision maker for
  an Approval event.
- **Parallel response:** After an Issue completes, one Agent can write release
  notes and another can notify the owner. Mark the earlier rule as `continue`.
- **Fallback and takeover:** Put a global fallback at the bottom and an
  Issue-specific rule above it. Order makes precedence visible without a
  separate priority calculation.

## Expressions

A rule matches event attributes with a Boolean expression. Operators include
`==`, `!=`, `&&`, `||`, `in`, and `startsWith`.

```text literal
# Approvals only for Issue #42
event.type == "com.mohist.workflow.stage.approval-requested" && event.issue == "42"

# Terminal Workflow failure anywhere in the Project
event.type == "com.mohist.workflow.run.failed"

# Every event under Issue #42
event.issue == "42"

# Completion of either of two Issues
event.type == "com.mohist.issue.completed" && event.issue in ["42", "43"]
```

Response prompts use the same attributes as placeholders, including
`{{event.issue}}`, `{{event.workflowrunid}}`, and `{{event.stage}}`. The Agent
uses them to retrieve details, decide, and act.

## Scenario: Supervise an Issue

The central scenario delegates Issue supervision to an Agent. It decides an
Approval, repairs a terminal failure, and involves the owner only when it stops.
Install the built-in Agent, routing rules, and prompts with one command:
`mo agent install supervisor`. See
[Agent Supervision](agent-supervision.md) for its behavior.

## Visibility

Mohist does not impose strict configuration conflict prevention. It provides
visibility so users can verify the intended routing:

- From an event, inspect which rule and Agent responded.
- From an AgentJob, inspect its triggering event and rule.
- Run a **dry run** against a recent event to evaluate every routing rule and
  show each match before waiting for a real event.

The user owns configuration correctness. The system owns observability.

## More Scenarios

The same routing table supports other scenarios:

- **Automatic completion summary:** when an Issue completes, summarize
  artifacts and write release notes.
- **Follow-up generation:** when an Issue completes or a review finds risk,
  create a follow-up Issue.
- **Production maintenance:** when a Runner disconnects or an Epic cannot
  advance an Issue, analyze the cause, notify the owner, or create a
  maintenance Issue.

These rules use one table and expression language. Only the condition and
response prompt differ.

## Responsibility Boundary

The user writes correct and safe response prompts and uses table order and
`continue` for exclusivity, fallback, or parallel response. The system matches
events accurately, starts Agents, and records the relationship between event
and response. The system does not judge whether a prompt is correct or give
Agents a privileged Approval path: Agents use the same supported path as the
owner and scripts; see [Workflow](the-workflow.md).
