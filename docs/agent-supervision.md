# Agent Supervision

Agent supervision delegates Approval Point and terminal failure handling to a
Mohist Agent. The Agent reviews artifacts, repairs recoverable failures, and
uses the same operations as a user. The user acts when the Agent cannot make a
safe decision or when a terminal or product-direction decision is required.

## Product Commitments

- A supervised Agent may Approve, Request Changes, repair, retry, or rerun within its authority.
- The user retains decisions about the Issue goal, complete-run stop, and Issue closure.
- Each supervision Agent writes an Issue comment with its configured marker and explains the action and reason.
- Notifications report an Approval Point, Workflow failure, or Agent failure. They do not replace an operator.
- The user can always take over through the regular command surface.
- Issue watch applies to one Issue. Project routing can supervise many Issues and can be muted for one Issue.

## Mental Model

The Agent is the front-line operator. It uses the same commands as the user to
approve, request changes, repair, and retry. The user handles exceptions.

A notification creates awareness, not a task. The user still receives notice at
an Approval Point or Workflow failure, while the supervised Agent performs the
operation when it can act safely. If the Agent cannot start or fails during its
response, Mohist sends a notice instead of leaving an unowned state.

Issue comments are the handoff surface. The default `supervisor` begins each
intervention with `[supervisor]`; a specialized Agent uses its configured marker.
Each comment records the decision, action, and reason. The user can read the
Issue and take over from those comments.

## Quick Start

Choose one scope according to the required trust:

```bash
# Put only one Issue on autopilot
mo issue watch add 42 --agent supervisor

# Or supervise the complete Project with an Agent and Project routing rules
mo agent install supervisor
```

The Agent must find the `mohist` Skill in the Issue Workspace. Install it when
missing:

```bash
mo skill install --path <repository-path>
```

## Issue Watch as an Autopilot Switch

A watch is an Issue-level switch. The selected Agent responds when that Issue
reaches an Approval Point or a terminal failure. Its behavior matches Project
supervision but applies only to that Issue.

`mo issue view 42` shows who supervises the Issue:

```text literal
Watching: supervisor   # This Agent is the Issue autopilot
Muted:    -            # Agents explicitly told not to handle this Issue
```

One command removes either source of supervision:

```bash
mo issue watch remove 42 --agent supervisor
```

- Removing an explicit watch returns the Issue to the user.
- Removing supervision supplied by a Project routing rule records a **mute**.
  The rule and other Issues remain unchanged. A later `watch add` removes the
  mute.

Mentioning `@supervisor supervise and advance this Issue` in a comment also
works. The Agent runs `mo issue watch add` to make the continuing watch
explicit.

## Approval Point Handling

At an Approval Point, the Agent reads the Issue goal and Stage artifacts. It
selects Approve when the artifacts serve the goal. It selects Request Changes
when a required change remains and states the change clearly. The bound
Feedback Tasks apply the Approval Feedback, and the Agent reviews the same
Approval Point again.

The Agent does not decide a product-direction trade-off or a question for which
it lacks information. It leaves the Approval Point waiting and writes a comment
that explains the uncertainty. The waiting Approval Point tells the user to act.

## Failure Handling

When a Workflow reaches terminal failure after automatic recovery is exhausted,
the Agent reads earlier intervention records, analyzes the cause, and chooses an
action.

When it can repair the problem, it fixes the Workspace and retries. When more
intervention cannot produce progress because the cause is unknown, the repair is
out of scope, or the failure repeats, it does not retry. It writes a comment
with its conclusion, attempted work, and required user decision, then stops.
The Run remains failed for the user.

Stopping is an Agent judgment, not a fixed retry count. Comments show repeated
lack of progress and prevent retrying by chance. Every failure remains visible
through notifications so the user can intervene earlier.

## Delegation Boundary

The Agent judges whether work is correct. The user decides whether work should
continue.

- **Agent:** Review artifacts, analyze failures, repair code, retry or rerun,
  select Request Changes, and give Approval Feedback.
- **User:** Close an Issue, stop the complete Run, or change the Issue goal.
  These terminal or directional decisions are not delegated. The Agent may
  recommend them in a comment but cannot decide them.

## Taking Over

The user acts when the Agent stops and requests a decision, the Agent response
fails, or production stalls and remains stopped. Read the comments, then use
normal commands to approve, request changes, or retry.

The Agent does not lock state. The user can always use the regular command
surface. `watch remove` removes the Agent from the Issue entirely.

## Customization

- Change Approval strictness with `mo agent edit supervisor` and edit the
  Instructions.
- Keep the final `integrate` delivery Approval for the user with
  `mo routing rule edit supervisor-approval` and the expression
  `event.type == "com.mohist.workflow.stage.approval-requested" && event.stage != "integrate"`.
- Mute an exception with `mo issue watch remove <issue> --agent supervisor`.
- Add a custom rule with `mo routing rule create` and order it with
  `mo routing rule move`. Supervisor rules remain fallback rules at the bottom
  and do not take precedence over custom rules.

### Specialized Agents

The default `supervisor` handles Approvals and failures. Their response
differences live in separate rule prompts. They share one identity as the
owner's proxy, so an Issue's Approval and failure history remains in one
memory. Split the role only when the responses need different models,
behavior, or concurrency limits.

Create another Agent with `mo agent create`, then use `mo routing rule edit` to
assign one rule to it. Give each Agent its own comment marker, such as
`[approver]` or `[fixer]`. Each Agent's Instructions must require reading all
supervision comments before acting, not only comments by that Agent. Otherwise
neither Agent can detect repeated Requests Changes that do not produce progress.

## Implementation Gaps

`mo agent install supervisor`, Issue watch and mute, Agent-response-failed
notifications, Approval actor records, routing tables, Agent launch, and
Approval and failure events are implemented. Specialized Agents are assembled
manually with `mo agent create` and `mo routing rule create`. See the
supervision scenario in [Agent Event Routing](event-routing.md) for prompt
guidance.
