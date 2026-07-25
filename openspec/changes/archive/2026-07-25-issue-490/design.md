## Context

Issue #490 adds the third Agent launch trigger promised in `docs/agents.md`: writing `@<agent>` in an
issue comment launches that Agent with the comment as input. The product/design contract is fixed in
[`design/agent-mentions.md`](../../../design/agent-mentions.md); this document resolves the implementation
seams and one contradiction in that contract (see Decision 1).

The foundation is already built by earlier issues:

- `IAgentLauncher` exposes two launch paths — `LaunchAsync` (manual, workspace-optional, GUID-keyed)
  and `LaunchRoutedAsync` (routing/watch, requires a resolved nonterminal workflow-run workspace,
  stable-keyed on `(projectId, eventId, ruleId)`).
- `AgentSessionResolver.StableSessionId/StableJobKey` hash an arbitrary trigger-identity string.
- `RoutedAgentLaunchContextResolver` resolves a routed CloudEvent to an ownership-validated
  workspace (and returns a typed preflight failure when no nonterminal run/workspace exists).
- `AddCommentAsync(author, body)` persists a comment row but emits **no event** today.
- `IssueLineage.BuildExtensions` stamps `projectid`/`issue`/`epic` on issue events; grains emit
  CloudEvents directly via `IEventStore.AppendAsync(db, envelope)` within their save transaction
  (the `EpicGrain` pattern).
- `mo issue comment add --author <name>` and the `POST /comments` route already carry a declared
  author; loop prevention relies on Agents authoring comments with their own name.

Stakeholders: the Agent-supervision epic (#58, "人可以离开回路"). Motivation and required behavior live
in [`proposal.md`](proposal.md) and [`specs/`](specs/).

## Goals / Non-Goals

**Goals:**

- Make adding an issue comment emit `com.mohist.issue.comment-added`, stamped with issue lineage.
- Detect `@<agent>` mentions in comment bodies and launch each named active Agent once.
- Make mention launch work on an issue **regardless of workflow-run state** (backlog, in-progress, or
  terminal) — the headline use case is a human pinging an Agent to push a backlog issue forward, after
  which the Agent itself runs `mo issue start`.
- Reuse the shared launcher and issue-context metadata so mention launches are observable like other
  Agent launches, with comment↔session provenance.
- Keep the change additive: no new persistent resource, no schema migration, no new CLI/API surface.

**Non-Goals:**

- No system reply / inbox entry for unresolved mentions (typos). Structured log only (open question).
- No expansion of a mention into a persistent watch/subscription — sustained attention is the Agent's
  own job via `mo issue watch add`.
- No mention support outside issue comments (issue body `@` stays a reference).
- No authentication of comment authorship — loop prevention is declaration-based (see Decision 5).

## Decisions

### Decision 1: Mention uses the manual-style launch path, not the routed path

`design/agent-mentions.md` says mention "复用路由启动的解析管线" (reuse the routed resolution
pipeline) and that "preflight 失败进失败 AgentJob". Taken literally this means reusing
`RoutedAgentLaunchContextResolver` + `LaunchRoutedAsync`, which **require a nonterminal workflow run
with a persisted workspace**. But the design's own headline example is `@supervisor 监督并推进这个issue`
→ the Agent runs `mo issue start 42`. On a backlog issue there is no run and no workspace, so the
routed resolver returns `IssueRunMissing` and records a **preflight-failed AgentJob with no running
Agent** — making the headline example impossible.

**Decision:** mention launches via `LaunchAsync` (the manual path), which is workspace-**optional** and
records only issue/epic context as session annotations. The Agent launches with no mounted workspace,
issues `mo issue start` (a server API call, workdir-independent), and the resulting run creates the
workspace for subsequent work. This reconciles the design doc: "reuse the routed pipeline" is read as
*reuse the shared launcher and issue-context resolution*, not the workspace-mandatory preflight gate.

**Alternative considered — reuse `LaunchRoutedAsync`:** maximally consistent with routing/watch, but
breaks the backlog use case (the primary motivation for the feature) and would generate
preflight-failure noise on exactly the issues most likely to be mentioned. Rejected.

**Alternative considered — hybrid (routed when a run exists, manual when not):** two code paths for
one feature, observable inconsistency (a mention behaves differently depending on issue state).
Rejected for simplicity.

### Decision 2: Comment-added is a direct CloudEvent, not an `IssueEvent` union variant

`AddCommentAsync` does not change Issue aggregate state (the comment is a side record), so routing it
through the `IssueEvent` union + `IssueEventSerializer` would pollute the state-transition event family
with a non-state event.

**Decision:** add an `EventCatalog.ReverseDns.IssueCommentAdded = "com.mohist.issue.comment-added"`
constant and a standalone payload record `{ commentId, author, body }`. `AddCommentAsync` appends the
CloudEvent via `IEventStore.AppendAsync(db, envelope)` inside its existing save transaction (the
`EpicGrain` pattern), stamps it with `IssueLineage.BuildExtensions(_issue)`, and pokes the dispatcher
after commit. The `IssueEvent` union and `IssueEventSerializer` are untouched.

**Alternative considered — model comment-added as an `IssueEvent` variant:** lets `IssueStore.SaveAsync`
emit it for free, but couples a non-state side effect to the aggregate's transition log and forces the
union/serializer to grow for every non-state event we add later. Rejected.

### Decision 3: Idempotency is anchored on `(projectId, commentId, agentId)`, comment-stable

The comment — not the delivering event's GUID — is the durable anchor. `AgentSessionResolver` gains a
stable-key entry that hashes `projectId\ncommentId\nagentId` (reusing the existing `StableId` helper).
`LaunchAsync` is extended (or a thin `LaunchMentionAsync` is added) to accept an explicit idempotency
anchor + trigger labels, so redelivery of a comment's event reuses one session grain and one AgentJob.

**Alternative considered — reuse `(projectId, eventId, ruleId)` by stuffing the comment into
`ruleId`:** would key on the event GUID, which is not the durable anchor the design specifies and
fragile to event re-derivation. Rejected.

### Decision 4: Token parsing is a single regex, name-only, case-insensitive, deduped by resolved Agent

Parse `@` immediately followed by a name token `[A-Za-z0-9_.-]+` (delimited by whitespace/punctuation),
case-insensitively. Resolve each distinct token via `AgentQuerier.GetByNameAsync` (active only);
dedupe by **resolved Agent id** (two tokens that resolve to the same Agent → one launch). Unresolved
tokens log and continue.

**Alternative considered — resolve by id when token looks like an id:** the design explicitly says
name-only ("只按名字解析，不解析 id"). Rejected.

### Decision 5: Loop prevention compares author name to active Agent names (declaration, not auth)

Before scanning, the handler loads the project's active Agents once; if the comment's `author`
matches any active Agent name (case-insensitive), it skips mention detection entirely. This relies on
the existing convention that Agents author comments with `--author <their name>`. It is a declaration,
not authentication: a human signing an Agent's name also produces a non-triggering comment. This is an
accepted cost in the local single-user model.

**Alternative considered — authenticate authorship via a signed Agent identity on comments:** no such
identity exists today and would be disproportionate to the single-user threat model. Rejected (open to
revisit under multi-user).

### Decision 6: Provenance via trigger labels — event id + a new comment-id label

Mention launches record `mohist.io/trigger/event-id` = the `comment-added` event id (so the existing
event↔session link works) and a new label `mohist.io/trigger/comment-id` = the comment id (so the
launch is findable from the comment side and distinguishable from routing/watch launches).

### Decision 7: An explicit `@` mention overrides `muted` watch declarations

`mute` (issue #489) is enforced inside `RoutingDispatchHandler` and governs the *automatic* launch
paths — routing-rule hits and watch launches. A mention is a different kind of trigger: a direct,
explicit human directive in a comment. **Decision:** `MentionDispatchHandler` SHALL NOT consult
`WatchEntryStore`; a `muted` Agent on an issue is still launched when a human explicitly `@`-mentions
it. Mute continues to suppress only automatic paths. This keeps the explicit-directive path
unconditional and predictable, and avoids coupling the mention handler to the watch data layer.

**Alternative considered — mute suppresses mentions too:** would let an operator fully silence an
Agent on an issue. Rejected: an explicit `@` is an intentional human action, and silently dropping it
(a comment that names an Agent yet nothing happens, with no rule to point at) is more confusing than
honoring it. If full silencing is ever needed, a dedicated "ignore mentions" control would be clearer
than overloading mute.

## Risks / Trade-offs

- [Author spoofing disables loop prevention] -> Accepted. A human (or a misconfigured Agent) signing
  an Agent's name suppresses triggers from that comment. Bounded to one comment; documented in the
  design spec. Revisit if/when multi-user authorship lands.
- [Mention on a terminal-issue comment still launches] -> By design (Decision 1): a mention is an
  explicit human directive and launches regardless of run state. The Agent observes terminal state via
  its own commands and decides what to do. This diverges from the routed path's terminal-run preflight
  failure — intentional, not a bug.
- [Two active Agents share a name] -> `GetByNameAsync` returns one row; mention resolves to one Agent.
  Name uniqueness is already enforced at Agent-create time; collision is a pre-existing data issue, not
  introduced here.
- [Bulk comment import emits many events] -> Each comment emits exactly one event and produces at most
  one launch per mentioned Agent; idempotency (Decision 3) and the one-shot semantic bound
  amplification. No persistent subscription is created.
- [Comment-added event volume] -> Additive to the bus; consumers are opt-in via `[Subscription]`, so
  only `MentionDispatchHandler` reacts. No existing handler subscribes to this type.

## Migration Plan

Additive — no data migration, no new table (the comment row already exists), no breaking API change.

1. Add `EventCatalog.ReverseDns.IssueCommentAdded`; inject `IEventStore` into `IssueGrain` and emit the
   event in `AddCommentAsync` within the existing transaction; poke the dispatcher after commit.
2. Add the `MentionDispatchHandler` (`[Subscription(Type = IssueCommentAdded)]`); wire loop prevention,
   token parsing, name resolution, and the mention launch.
3. Extend `AgentSessionResolver` (comment-anchored stable key) and `IAgentLauncher` (mention anchor +
   comment-id trigger label); add the `mohist.io/trigger/comment-id` metadata constant.
4. Specs: unit tests for token parsing, loop prevention, resolution-failure no-op, and idempotency;
  spec tests for end-to-end comment→launch and redelivery-dedup, using the existing
  `RoutingDispatchTestSupport`-style fakes (no real bus/DB/time).

**Rollback:** revert the emission and the handler. Comments continue to work (the event is purely
additive); no data to migrate back. Mention launches already created are ordinary AgentSessions and
need no cleanup.

## Open Questions

- **Typo feedback for unresolved mentions.** Today the only signal is a structured log + "nothing
  happens". A system reply comment or an inbox entry would close the loop. Deferred per
  `design/agent-mentions.md` until real usage shows the need.
