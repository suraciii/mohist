# Comment Mentions

Writing `@<agent-name>` in an Issue comment starts that Mohist Agent directly.
A mention is a direct Agent trigger, not a configurable RoutingRule or a
subscription. It requires no configuration because the comment author selects
the Agent by name.

See [`event-protocol.md`](event-protocol.md) for event protocol and stamping.
See [`agent-execution.md`](agent-execution.md) and
[`event-routing.md`](event-routing.md) for launch, AgentJob, and AgentSession.

## Design Drivers

- The comment author has already selected the Agent. A second configurable
  routing decision would add ambiguity without adding control.
- The committed comment and its durable event form one stable intent boundary.
  Redelivery must not create multiple AgentJobs.
- Agent output can contain mentions. Agent-authored comments must stop at the
  trigger boundary or one response can recursively create work.
- A mention is one bounded request. Continued responsibility must be explicit
  through Issue watch so the owner can see and revoke it.

## Model

A mention adds no domain resource. It uses existing concepts:

- **Comment:** An ordinary Issue comment emits the
  `com.mohist.issue.comment-added` event family. It uses `issue.*` stamping
  with `projectid`, `issue`, and optional `epic`. Its payload contains
  `commentId`, `author`, and `body`.
- **Mention token:** An Agent name after `@`, delimited by whitespace or
  punctuation, matched without case sensitivity. Resolution uses name, not ID.
- **Trigger:** One resolved mention performs one ordinary Agent launch. It
  produces an AgentJob and Agent-launch-origin AgentSession. The prompt is the
  complete comment body, including the `@` token.

Only Issue comments trigger mentions. An `@` in an Issue body is a reference
and does not trigger.

## Semantics

### Detection and launch

```text diagram
  +----------------------+
  | Issue comment commit |
  +-----------+----------+
              |
              v
  +-----------------------+
  | durable comment-added |
  |         event         |
  +-----------+-----------+
    +---------+----------+
    vAgent-authored      vowner-authored
+------+    +-------------------------+
| stop |    | resolve distinct @names |
+------+    +------------+------------+
                         |
                         v
             +-----------------------+
             | ordinary Agent launch |
             +-----------------------+
```

A mention uses the shared manual Agent launcher with an optional Workspace. It
does not use routing launch, resolve Workspace, or run preflight. For example,
a person can ask `@supervisor` to advance a Backlog Issue with no WorkflowRun
or Workspace. Routing preflight would create a failed AgentJob for this valid
mention path.

Trigger labels record comment ID and comment-added event ID. They support
navigation in both directions and distinguish mention launch from routing or
watch launch.

The idempotency key includes `commentId`:

- redelivery of one comment starts no second AgentJob;
- repeated mention of one Agent in one comment launches once;
- distinct Agent names in one comment each launch once.

Each launch is an ordinary AgentJob. A mention does not create a durable
subscription. An Agent that needs continued supervision must explicitly run
`mo issue watch add`; see [`issue-watch.md`](issue-watch.md).

### Loop prevention

An Agent comment declares its name through `--display-name`; preset text includes
this rule. A comment whose author matches an active Project Agent name is not
scanned for mentions. Agent-authored comments therefore trigger neither another
Agent nor the authoring Agent.

Display name is a declaration, not authentication. A person who deliberately
uses an Agent name also suppresses mention detection. This convention is
accepted for local single-user use.

### Resolution failure

An unknown Agent name remains an ordinary comment and starts nothing. Mohist
records a structured log. A person can use `mo agent job list <agent>` to
confirm launch. A misspelled name currently produces only the absence of work.
Explicit feedback through a system comment or inbox remains open.

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

## Open Question

Should an unresolved mention produce an explicit system comment or inbox item?
Until usage data answers this, keep structured logging and no visible action.

## Status

Comment creation and its lineage-stamped event commit together. Mention
parsing and Agent name resolution are case-insensitive. Comment identity makes
launch idempotent. Agent-authored comments cannot recurse. A muted watch does
not suppress an explicit mention. Mention launch reuses the ordinary Agent
launch path rather than creating a second execution contract.

The durable dependency is the comment-author attribution convention in
[`event-response.md`](event-response.md). It lets the event path distinguish a
person's explicit mention from Agent-authored output without coupling the design
to a handler or launcher class.
