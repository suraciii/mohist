# Agent Supervision

After an Issue enters a Workflow, stages require Approvals and failures require
handling. Agent supervision delegates these front-line decisions to a Mohist
Agent. It decides Approvals and analyzes or repairs failures. The user acts only
when the Agent cannot continue.

## Mental Model

- **The Agent is the front-line operator; the user handles exceptions.** The
  Agent uses the same commands as the user to approve, reject, repair, and
  retry. The user does not need to be present.
- **A notification creates awareness, not a task.** The user still receives a
  notice at an Approval point or Workflow failure. The actual call to action is
  either the Agent's stop comment or production that remains stopped.
- **Agent failures also notify the user.** A notice reports when the Agent
  cannot start or fails during its response. There is no silent state in which
  the user assumes an Agent is handling work that has no operator.
- **Issue comments are the handoff surface.** Every Agent intervention writes a
  comment beginning with `[supervisor]` that explains its decision, action, and
  reason. The user can open the Issue and take over from those comments.

## Quick Start

Choose one of two scopes according to the required trust:

```bash
# Put only one Issue on autopilot
mo issue watch add 42 --agent supervisor

# Or supervise the complete Project with an Agent and Project routing rules
mo agent install supervisor
```

The Agent must find the `mohist` Skill, which documents the `mo` command
surface, in the Issue workspace. Install it when missing:

```bash
mo skill install --path <repository-path>
```

## Issue Watch as an Autopilot Switch

A watch is an Issue-level switch. The selected Agent responds when that Issue
reaches an Approval point or a terminal failure. Its behavior matches Project
supervision but applies only to that Issue.

`mo issue view 42` shows who supervises the Issue:

```text
Watching: supervisor   # This Agent is the Issue autopilot
Muted:    -            # Agents explicitly told not to handle this Issue
```

One command removes either source of supervision:

```bash
mo issue watch remove 42 --agent supervisor
```

- When supervision came from `watch add`, removal returns the Issue to the
  user.
- When supervision came from a Project routing rule, removal records a
  **mute**. The global rule and other Issues remain unchanged, but the user
  handles Issue #42. A later `watch add` removes the mute.

Mentioning `@supervisor supervise and advance this Issue` in a comment also
works. The Agent runs `mo issue watch add` to make the continuing watch
explicit.

## Approval Handling

At an Approval point, the Agent reads the Issue goal and stage artifacts. It
approves when the artifacts serve the goal. It rejects when a required change
remains and states the change clearly. Rejection creates a feedback task for
automatic rework. The Agent receives the next Approval request and reviews
again.

The Agent does not decide a product-direction trade-off or a question for which
it lacks information. It leaves the Approval waiting and writes a comment that
explains the uncertainty. The waiting Approval is the user's signal to act.

## Failure Handling

When a Workflow reaches terminal failure after automatic recovery is exhausted,
the Agent first reads its earlier intervention records. It analyzes the root
cause and chooses an action. When it can repair the problem, it fixes the
workspace and retries. When more intervention would not produce progress
because the cause is unknown, the repair is out of scope, or the same failure
repeats, it does not retry. It writes a comment with its conclusion, attempted
work, and required user decision, then stops. The run remains failed for the
user.

Stopping is an Agent judgment, not a fixed retry count. The Agent uses comments
to recognize repeated lack of progress and hands the problem to the user rather
than retrying by chance. Every failure remains visible through notifications,
so the user can intervene earlier.

## Delegation Boundary

The Agent judges whether work is correct. The user decides whether work should
continue.

- **Agent:** Review artifacts, analyze failures, repair code, retry or rerun,
  reject an Approval, and give rework feedback.
- **User:** Close an Issue, stop the complete run, or change the Issue goal.
  These terminal or directional decisions are not delegated. The Agent can
  recommend them in a comment but cannot decide them.

## Taking Over

The user acts in three cases: the Agent stops and requests a decision in a
comment, the Agent response fails and sends a notification, or production
stalls and remains stopped. Use normal commands to approve, reject, or retry,
and read comments for context first. The Agent does not lock state. The user can
always act through the regular command surface. `watch remove` removes the Agent
from the Issue entirely.

## Customization

- Change Approval strictness with `mo agent edit supervisor` and edit the
  Instructions.
- Keep the final `integrate` delivery Approval for the user with
  `mo routing rule edit supervisor-approval` and the expression
  `event.type == "com.mohist.workflow.stage.approval-requested" && event.stage != "integrate"`.
- Mute an exception with
  `mo issue watch remove <issue> --agent supervisor`.
- Add a custom rule with `mo routing rule create` and order it with
  `mo routing rule move`. Supervisor rules remain fallback rules at the bottom
  and do not take precedence over custom rules.

### Specialized Agents

The default `supervisor` handles both Approvals and failures. Their response
differences live in separate rule prompts. They share one identity as the
owner's proxy, so an Issue's Approval and failure history remains in one memory.
Split the role only when the two responses need different models, separate
behavior, or independent concurrency limits.

No new mechanism is required. Create another Agent with `mo agent create`, then
use `mo routing rule edit` to assign one rule to it. Observe two constraints:

- Give each Agent its own comment marker, such as `[approver]` or `[fixer]`, so
  each can count its own interventions.
- In each Agent's Instructions, require reading **all** supervision comments on
  the Issue before acting, not only comments by that Agent. Otherwise neither
  side can see a loop between repeated rejection and repeated repair.

## Implementation Status

`mo agent install supervisor`, Issue watch and mute, Agent-response-failed
notifications, Approval actor records, routing tables, Agent launch, and
Approval and failure events are implemented. To assemble specialized Agents
manually, use `mo agent create` and `mo routing rule create`. See the supervision
scenario in [Agent Event Routing](event-routing.md) for prompt guidance.
