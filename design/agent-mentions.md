---
status: converged
---

# Comment Mentions

Writing `@<agent-name>` in an Issue comment starts that Mohist Agent directly.
A mention is the third Agent trigger after manual launch and routing rules. It
requires no configuration because the name in the comment is the routing
decision.

See [`event-protocol.md`](event-protocol.md) for event protocol and stamping.
See [`agent-execution.md`](agent-execution.md) and
[`event-routing.md`](event-routing.md) for launch, AgentJob, and AgentSession.

## Design Drivers

A comment mention is intentionally a direct trigger rather than another RoutingRule:

- The author has already selected the Agent by name, so a second configurable routing decision
  would add ambiguity without adding control.
- The comment and its durable event provide a stable intent boundary. Delivery can be retried
  without turning one comment into several AgentJobs.
- Agent output can itself contain mentions. Attribution must therefore stop Agent-authored
  comments at the trigger boundary or an ordinary response could recursively create work.
- A mention is one bounded request, not a subscription. Continued responsibility must be explicit
  through Issue watch so the owner can see and revoke it.

These forces keep mention handling small: recognize durable user intent, admit one ordinary Agent
launch, and leave execution ownership to the existing Agent path.

## Model

A mention adds no domain resource. It uses existing concepts:

- **Comment:** An ordinary Issue comment emits a new
  `com.mohist.issue.comment-added` event family. It uses `issue.*` stamping with
  `projectid`, `issue`, and optional `epic`. Payload contains `commentId`,
  `author`, and `body`.
- **Mention token:** An Agent name after `@` in the body, delimited by
  whitespace or punctuation and matched without case sensitivity. Resolve only
  by name, not ID.
- **Trigger:** One mention performs one ordinary Agent launch, producing an
  AgentJob and Agent-launch-origin AgentSession. Prompt is the complete comment
  body and retains the `@` token.

Only Issue comments trigger mentions. `@` in an Issue body is a reference and
does not trigger.

## Semantics

### Detection and Launch

```text diagram
Issue comment commit
        |
        v
durable comment-added event
        |
        +-- Agent-authored --> stop
        |
        +-- owner-authored --> resolve distinct @names
                                  |
                                  +--> ordinary Agent launch
```

- Launch uses the shared launcher's manual, Workspace-optional path instead of
  routing launch. Issue context is Session metadata, but launch does not resolve
  Workspace or run preflight. A common mention asks `@supervisor` to advance a
  Backlog Issue that has no WorkflowRun or Workspace. Routing preflight would
  create a failed AgentJob for the mention that most needs to work. Trigger
  labels record comment ID and comment-added event ID for navigation in both
  directions and distinguish mention from routing or watch.
- The idempotency key includes commentId. Redelivery of one comment does not
  launch again. Repeated mention of one Agent in one comment launches once.
  Different Agents each launch.
- Mention is a one-time AgentJob. When an owner requests continuing supervision,
  the Agent makes it explicit with `mo issue watch add`; see
  [`issue-watch.md`](issue-watch.md). The system does not turn a mention into a
  durable subscription.

### Loop Prevention

By convention, an Agent comment declares its name through `--display-name`; preset
text includes this rule. A comment whose author matches an active Project Agent
name is not scanned for mentions. An Agent comment therefore triggers neither
another Agent nor itself. A mention chain can begin only with a person's
comment.

Display name is a declaration, not authentication. A person who deliberately
uses an Agent name also suppresses mention detection. This convention cost is
acceptable for local single-user use.

### Resolution Failure

A mention of an unknown name remains an ordinary comment and starts nothing. A
structured log records it. A person can use `mo agent job list <agent>` to
confirm launch. A misspelled name currently produces only the absence of work.
Explicit feedback through system comment or inbox remains an open question.

## Example

```text literal
# The owner comments on Issue #42 through Web or mo issue comment create:
@supervisor supervise and advance this Issue

# Mohist starts one supervisor AgentJob with the complete comment as Prompt
# and Issue #42 as context. Typical Agent actions are:
#   mo issue start 42
#   mo issue watch add 42 --agent supervisor
# The Agent records its plan in a comment marked [supervisor].
```

## Status

The capability is implemented. Comment creation and its lineage-stamped event
commit together; mention parsing and Agent name resolution are case-insensitive;
comment identity makes launch idempotent; Agent-authored comments cannot recurse;
and a muted watch does not suppress an explicit mention. Mention launch reuses
the ordinary Agent launch path rather than creating a second execution contract.

### Open Question

Should an unresolved mention produce an explicit system comment or inbox item?
Keep structured logging and no visible action until real usage data answers.

The durable dependency is the comment-author attribution convention in
[`event-response.md`](event-response.md). It lets the event path distinguish an
owner's explicit mention from Agent-authored output without coupling the design
to a handler or launcher class.
