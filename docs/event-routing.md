# Agent Event Routing

Event routing starts a Mohist Agent when a Project event matches a configured
rule. The rule selects the Agent and supplies the response prompt. The Agent
uses the same commands as a human owner. See [Agents and AgentSessions](agent-sessions.md)
for Agent execution and [Event Protocol](../design/event-protocol.md) for event
attributes and expressions.

## Product Commitments

- A Project owns one ordered routing table.
- A rule names the event condition, responding Agent, and response prompt.
- Mohist evaluates rules from top to bottom. The first matching rule wins by
  default.
- `continue` lets later matching rules respond to the same event.
- A matching rule starts one AgentJob, AgentSession, SessionInput, and
  AgentTurn through the normal Agent launch path.
- Event attributes carry business lineage, so one expression can cover all
  events under an Issue, Epic, or WorkflowRun.
- Agent Instructions define identity. A rule response prompt defines the
  reaction.
- Routing does not create a privileged Agent channel or a second command
  surface.
- Users can inspect the matching rule, responding Agent, and resulting work.

## Event Model

Workflow, Issue, Epic, Runner, Workspace, and AgentSession events can trigger a
response. Each event carries attributes such as `event.type`, `event.issue`,
`event.epic`, `event.workflowrunid`, and `event.stage` when the event family
promotes them.

A rule matches these attributes with a Boolean expression. It does not match
private payload data. The same attributes can appear in the response prompt as
placeholders.

## Configure Rules

A Project's ordered table contains rules. One Agent can have several rules.
The Agent subscription view shows the same rules filtered by Agent; it does not
create another subscription resource or change table order.

Agent Instructions remain shared by all rules for that Agent. A response prompt
belongs to one rule. This keeps identity reusable and reaction-specific
instructions local.

## Evaluate Rules

Mohist evaluates active rules in table order:

```text diagram
      +------------+
      | CloudEvent |
      +------+-----+
             |
             v
 +-----------------------+
 | Project routing table |
 +-----------+-----------+
             |
             v
 +----------------------+
 | ordered active rules |<-----+
 +-----------+----------+      |
             |                 |
             v                 |
        +--------+             |
        | Match? +-------------++
        +----+---+             ||
             +--+              ||
                vyes           ||
      +-------------------+    ||
      | launch Agent once |    ||
      +---------+---------+    ||
                |              ||
                v              ||
          +-----------+        ||
          | Continue? |        ||
          +-----+-----+        ||
         +------+------+       ||
         vyes          vno     ||
   +-----------+   +------+  no||
   | next rule +<--| stop |----++
   +-----------+   +------+
```

- A Project event without a Project identity matches no table.
- A false condition advances to the next rule.
- A match renders the response prompt and starts the named Agent.
- An archived Agent or empty rendered prompt is skipped and recorded as a
  structured routing result.
- An expression evaluation error is treated as no match.
- If another trigger already launched the same Agent for the event, Mohist
  skips the duplicate and keeps the first rule for attribution.
- A matching Agent uses current domain state and the normal command surface;
  the event is a trigger, not a complete state snapshot.

Table order provides exclusive response, fallback, and takeover behavior.
`continue` provides parallel response. There is no numeric priority setting.

## Expressions

Operators include `==`, `!=`, `&&`, `||`, `in`, and `startsWith`.

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

Response prompts use the same attributes, including `{{event.issue}}`,
`{{event.workflowrunid}}`, and `{{event.stage}}`. An Agent uses them to retrieve
current details, decide, and act.

## Supervise an Issue

The built-in supervisor Agent can decide an Approval, repair a terminal
failure, and involve the owner when it stops. Install it with:
`mo agent install supervisor`. See [Agent Supervision](agent-supervision.md).

The Agent is a proxy for an owner, not a privileged channel. It uses the same
supported actions as the owner and scripts.

## Visibility

Users can:

- inspect which rule and Agent responded to an event;
- inspect an AgentJob's triggering event and rule;
- run a dry run against a recent event to evaluate every rule before waiting
  for a real event.

The user owns prompt and order correctness. Mohist owns event matching,
launching, and response evidence.

## Other Uses

The same table supports completion summaries, follow-up Issue creation, and
Runner or Epic maintenance. Only the condition and response prompt change.

## Boundary

The user defines safe prompts and table order. Mohist matches events, starts
Agents, and records attribution. Mohist does not judge prompt quality or give
Agents a private Approval path.
