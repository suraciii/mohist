## Why

Mohist Agents can be started two ways today — manual launch and project-level routing rules. Both
need configuration before they fire: a rule has to be written, a name has to be picked in the UI. For
the common, ad-hoc case ("hey supervisor, push this issue forward") there is no zero-config path. Issue
#490 adds the missing third launch trigger already promised in
[`docs/agents.md`](../../docs/agents.md): write `@<agent>` in an issue comment and that Agent starts with
the comment as its input. The design contract is fixed in
[`design/agent-mentions.md`](../../design/agent-mentions.md); the foundation it depends on
(`IAgentLauncher` dual-path launch + idempotency keys, issue workspace resolution, the comment
`author` field) is already built by issues #489 / #449.

## What Changes

- Adding an issue comment now emits a new `com.mohist.issue.comment-added` event, stamped with issue
  lineage (`projectid`, `issue`, `epic` if present) and carrying `commentId`, `author`, and `body`.
  Today `AddCommentAsync` only persists the row and emits nothing.
- A new system handler (`MentionDispatchHandler`, subscribing to `comment-added`) scans the comment
  body for `@<token>` mentions, resolves each token to an active Agent **by name** (case-insensitive,
  whitespace/punctuation-delimited, no id lookup), and launches each resolved Agent once via the
  shared routed-launch path — `prompt` is the full comment body verbatim (the `@` token is left in),
  context is the issue.
- **Loop prevention**: a comment whose `author` matches the name of any active Agent in the project is
  never scanned for mentions. Agent-authored comments therefore neither trigger other Agents nor
  re-trigger themselves; the mention chain can only start from a human comment. The convention that
  Agents author comments with `--author <their name>` is already in place.
- **Resolution failure is a no-op**: `@`-ing a name with no matching active Agent starts nothing and
  emits a structured log (the only signal of a typo is "nothing happens").
- **Idempotency** is keyed on `hash(projectId, commentId, agentId)`: the same comment redelivered does
  not relaunch; `@`-ing the same Agent multiple times in one comment launches once; `@`-ing several
  different Agents launches each independently. Trigger labels annotate `comment-id` + event id so the
  launch is traceable back to the comment from both the AgentJob and the comment side.
- A mention launch is a one-shot AgentJob. When the owner wants sustained attention, the Agent
  fulfills it itself with `mo issue watch add` (issue #489) — the system does not expand a mention into
  a persistent subscription.

## Capabilities

- `issue-comment-event`: The comment lifecycle becomes a first-class event source —
  `AddCommentAsync` emits `com.mohist.issue.comment-added` stamped with issue lineage and carrying
  `commentId` / `author` / `body`. This is the structural foundation that makes comment-driven
  automation (mention detection today) possible; owned by the Issue aggregate, independent of any
  consumer.
- `comment-mention`: The mention-triggered launch behavior at comment time — `@<token>` parsing,
  active-Agent-by-name resolution, loop prevention (author ≠ active Agent name), resolution-failure
  no-op, idempotency `hash(projectId, commentId, agentId)`, prompt = comment body verbatim, and reuse
  of the routed launch path (workspace resolution, preflight, trigger-label provenance).

## Impact

- **Server — Issue aggregate** (`packages/server/src/Mohist.Server/Issue/`): `IssueGrain.AddCommentAsync`
  records/emits the new `IssueCommentAdded` domain event; the issue event family + serializer
  (`Issue.Domain.Events.IssueEvent`, `Infrastructure/Events/IssueEventSerializer.cs`,
  `EventCatalog.cs`) gains `comment-added`.
- **Server — dispatch** (`packages/server/src/Mohist.Server/Events/Subscriptions/`): new
  `MentionDispatchHandler` system handler subscribing to `comment-added`; reuses
  `IAgentLauncher.LaunchRoutedAsync`, `AgentQuerier.GetByNameAsync` / `ListAsync` (active set for
  loop-prevention), and the issue workspace resolver — same collaborators as `RoutingDispatchHandler`.
- **Server — no new resource / no persistence migration**: mentions introduce no new aggregate or
  table (idempotency is keyed off the comment id, which already exists in `IssueComments`).
- **CLI / Web / API**: no new commands or routes. The user-facing surface is unchanged — write a
  comment (Web or `mo issue comment add --author <name>`) containing `@<agent>`. No public API
  removals or breaking changes.
- No new external dependencies.
