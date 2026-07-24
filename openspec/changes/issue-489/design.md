## Context

Issue #489 adds a per-issue autopilot surface on top of the existing event→agent
dispatch machinery. Today the only way to auto-respond to an issue is a project-level
routing rule (`RoutingRuleStore`); there is no per-issue toggle and no way to say "this
one issue, I'll handle myself" without editing the global rule. Watch introduces a
lightweight, issue-scoped declaration owned by the **Agent context** (not the Issue
aggregate), plus two dispatch behaviors: muted suppression of routing hits, and
watch-triggered launches on a fixed event set.

The product/semantic contract is already fixed in
[`design/issue-watch.md`](../../../design/issue-watch.md) (status: wip, all unimplemented)
and the two capability specs under `specs/`. This document covers only **how** to implement
that contract against the current codebase.

**Current dispatch path** (`packages/server/src/Mohist.Server/Events/Subscriptions/RoutingDispatchHandler.cs:29-109`):
on each `CloudEvent`, read `projectId` from lineage (`:31`), load active routing rules
(`:39`), evaluate them envelope-only via `RoutingTableEvaluator` (`:49`), and for each
matched, active agent resolve an execution context, handle preflight failures
(`RecordPreflightFailureAsync`, `:117-165`), then `IAgentLauncher.LaunchRoutedAsync`
(`:100-107`). Launch idempotency/replay-safety is delegated to Orleans grains keyed by
stable hashes of `(projectId, eventId, ruleId)` — see
`AgentSessionResolver.StableSessionId/StableJobKey`
(`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionResolver.cs:54-67`) and
first-writer semantics in `AgentJobGrain.EnsurePreparedAsync`
(`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:376-402`).

**Constraints:**
- WatchEntry is owned by the Agent context; the Issue aggregate must not hold it
  (`design/issue-watch.md`, model). Issue detail surfaces it only as a read projection.
- Fixed watch event set: `stage.approval-requested` and `run.failed` only — not configurable.
- No per-watch `ResponsePrompt`; discipline lives in Agent identity instructions.
- Watch launches must reuse the routed launch path (workspace resolution, preflight handling,
  trigger tagging).
- One Agent launched at most once per event, whether hit by a rule, a watch, or both.

## Goals / Non-Goals

**Goals:**
- Persist `WatchEntry(ProjectId, IssueNumber, AgentId, State)` as an Agent-context side
  relation with the `watch add | remove | list` state machine and idempotency.
- At dispatch time: suppress routing-rule hits that are `muted` on the event's issue, and
  launch `watching` agents on the fixed event set using the built-in prompt — both reusing
  the existing launch/preflight path.
- Guarantee one launch per `(event, agent)` even when a rule and a watch coincide.
- Project watching/muted read-only into `mo issue view`, `watch list`, and the Web issue
  detail from the single enriched `IssueReadModel`.

**Non-Goals** (per proposal/specs):
- Configurable watch event sets or per-watch response prompts.
- Web-side add/remove controls (read-only this issue; operations via CLI).
- Touching routing-rule semantics, ordering, or the launcher's launch contract.

## Decisions

### D1 — WatchEntry is a flat-column side relation, not Issue-grain state

Persist `WatchEntry` as a new `WatchEntryRow` (flat columns) mirroring `RoutingRuleRow`
(`packages/server/src/Mohist.Server/Infrastructure/Data/Agent/RoutingRuleRow.cs`), with
EF config in `MohistDbContext` mirroring the `RoutingRules` block (`MohistDbContext.cs:343-365`).
Keyed and uniquely indexed on `(ProjectId, IssueNumber, AgentId)`; secondary indexes on
`(ProjectId, IssueNumber)` (projection) and `(ProjectId, IssueNumber, State)` (watch-launch
queries). Domain model `WatchEntry` + `WatchEntryState { watching, muted }` mirrors
`Agent/Domain/RoutingRule.cs`. `WatchEntryStore : IScopedService` mirrors `RoutingRuleStore`
(`Agent/Services/RoutingRuleStore.cs:11`), reusing its active-Agent `ValidateAsync` pattern
(`:150-166`) verbatim, with a new `WatchEntryValidationException(code)`.

Flat columns (not the JSON-blob style used by `AgentRow`) because WatchEntry has few fields
and is filtered/indexed on several of them.

**Why a side relation, not Issue-grain state:** the design contract fixes ownership in the
Agent context. The Issue aggregate must not hold watchers, so mutations bypass `IIssueGrain`.
This matches how `IssueCommentRow`/`IssuePrerequisiteRow` already live as side relations
projected into the read model.

### D2 — Mutation API calls the store, then re-queries the enriched read model

New `IssueRoutes.Watch.cs` partial with `MapIssueWatch(this RouteGroupBuilder group)`, mounted
in `IssueRoutes.cs` beside the existing `MapIssueXxx` calls. POST add and DELETE remove
resolve the project (existing `ProjectResolutionEndpointFilter`), validate the issue exists via
`IssueQuerier.GetAsync`, call `WatchEntryStore`, and return `issuesQuery.GetAsync(...)` — the
re-enriched read model — exactly one round-trip, mirroring the prerequisites routes'
return-shape (`Api/IssueRoutes.Prerequisites.cs`).

**Deliberate deviation from the prerequisites mirror:** prerequisites routes go through
`IIssueGrain.AddPrerequisiteAsync` because prerequisites *are* Issue-grain state. Watch is
*not*, so the route calls `WatchEntryStore` directly. The shared helper
`GetIssueGrainAsync` (`Api/IssueRoutes.Helpers.cs:19`) is used only for existence resolution,
not for the mutation.

`list` needs no endpoint — it is the GET detail projection (D6).

### D3 — `watch remove` creates `muted` unconditionally when no declaration exists

The state machine (`design/issue-watch.md`, Semantics):

```
add:    none -> watching ; muted -> watching ; watching -> idempotent
remove: watching -> delete ; none -> muted ; muted -> idempotent
```

`remove` does **not** verify that a project-level routing rule actually covers the agent.
"Otherwise covered only by a project-level routing rule" is the *intent* of muting, not a
precondition the command checks. A mute with no covering rule is a harmless runtime no-op
(suppression that matches nothing) and decoupling the command from rule evaluation keeps it
simple, fast, and correct under rule edits between command and dispatch.

### D4 — Muted suppression lives in the dispatch handler, not the evaluator

Insert the muted check in `RoutingDispatchHandler.DispatchAsync` **after** the issue number is
resolved (post `:94`) and **before** `LaunchRoutedAsync` (`:100`). It mirrors the archived-skip
disposition (Layer 3 at `:64-66`): `continue` + a `LogWarning` with named tokens (`{EventId}`,
`{IssueNumber}`, `{AgentId}`/`{RuleId}`).

**Why not in `RoutingTableEvaluator`:** the evaluator is envelope-only by design (the comment
at `RoutingDispatchHandler.cs:46-48` states issue state must not affect rule selection). Muted
is per-issue, so it cannot be expressed in the envelope-only matching layer without leaking
DB state into rule selection. Suppression is therefore a dispatch-time gate, exactly where the
existing archived re-check already lives.

### D5 — Watch launch is folded into the same dispatch flow as routing

The watching-launch pass runs in `RoutingDispatchHandler` immediately after the rule loop
(post `:108`), guarded by `evt.Type ∈ {approval-requested, run.failed}` and the presence of an
issue in the envelope. For each `watching` entry it resolves the agent (active check), resolves
the execution context, reuses `RecordPreflightFailureAsync` for the not-ready case, and calls
`LaunchRoutedAsync` with the built-in watch prompt.

**This requires removing the handler's early return when `rules.Count == 0`** (`:40-41`) —
watch launches must fire even for projects with zero routing rules.

**Alternative considered — a separate `WatchDispatchHandler` with
`[Subscription(Type="...approval-requested|...run.failed")]`** (idiomatic; see
`WorkflowStageLockReleaseHandler.cs:25`). Rejected because muted suppression and watch launch
must share a single per-event `(eventId, agentId)` dedup scope (D7) and a deterministic
ordering (suppression before any launches). Splitting them forces cross-handler state sharing
with no benefit. The handler's responsibility is restated as *"dispatch events to agents via
routing rules and watches, applying mutes"*; watch logic is kept in clearly-named private
helpers (`ApplyMutedSuppression`, `LaunchWatchingAgents`) so the method reads as one flow.

### D6 — Projection: new batched block in `IssueQuerier.EnrichAsync`

Add `Watching` and `Muted` collections (default `[]`) to `IssueReadModel`
(`Issue/Services/IssueReadModel.cs:10`). Populate them in a new batched block inside
`IssueQuerier.EnrichAsync` (`Issue/Services/IssueQuerier.cs:525`), structurally beside the
comments/attachments block (`:552-586`): select WatchEntry rows where
`ProjectId == projectId && numbers.Contains(row.IssueNumber)`, group by `IssueNumber`, assign.

This is a cross-context read (Issue querier reading an Agent-context table) through the single
shared `MohistDbContext` — already the established pattern for every other projected relation.

### D7 — Per-event single-launch dedup is a local set in the handler, not a launcher change

Track launched `agentId`s for the current event in a local `HashSet<string>` within
`DispatchAsync`. The routing loop adds each launched agent; the watch loop skips any agent
already in the set. This satisfies the spec's *"same Agent hit by both a routing rule and a
watch on one event launches only once"* without touching the launcher.

**Alternative considered — normalize the launcher's stable key from `ruleId` to `agentId`.**
Rejected: routing legitimately allows the *same* agent to be matched by *multiple* rules on
one event (different `ruleId`s), and that currently yields distinct launches. Normalizing to
`agentId` would silently change routing semantics. Handler-level dedup leaves the launch
contract and routing behavior untouched. Grain first-writer semantics
(`AgentJobGrain.EnsurePreparedAsync`) still protect against event *replay* for each distinct
launch source.

### D8 — Watch provenance via a `watch:`-prefixed `TriggerRuleId`; built-in prompt composed at launch

Watch launches pass a `watch:`-prefixed identifier (e.g. `watch:{agentId}`) as the `ruleId`
arg to `LaunchRoutedAsync`. That value becomes the `TriggerRuleId` label stamped in
`AgentJobGrain.AdvancePreparedLaunchAsync` (`Agent/Grains/AgentJobGrain.cs:441-456`), so
event↔session traceability records the watch source without a schema change. The built-in
prompt is composed at launch time (event type + issue context + "act on your identity
instructions"); no per-watch `ResponsePrompt`.

**Alternative considered — a dedicated `TriggerSourceKind` label** (`routing | watch`) on
`GenericAgentSessionMetadata`. More explicit, but ripples into the computed columns
(`MohistDbContext.cs:181-183`) and `AgentSessionQuery` mapping. Deferred; the `watch:` prefix
is sufficient for this issue and downstream filters can match it.

### D9 — CLI: new command group; shared agent resolver; state-as-truth rendering

New `MohistCliCommands.Issue.Watch.cs` (partial `IssueCommands`) adds `BuildWatch(api)` → a
`watch` Command with `add`/`remove`/`list` leaves, registered in `MohistCliCommands.Issue.cs`.
Reuse `ResolveAgentAsync` + `AgentRef` by promoting them from `private` to a shared `internal`
helper in `MohistCliCommands.Agent.cs:802` (precedent: `VariableCommands` is shared across
command groups). Render via the newer `PrintMutationResourceAsync`/`PrintResourceAsync` path:
`add`/`remove` render `IssueShow` (idempotent — the resulting state is the source of truth,
matching `mo issue start` at `MohistCliCommands.Issue.Lifecycle.cs:35-41`); `list` renders the
same detail with the watching/muted sections. `agent_not_found` / `agent_archived` need no
CLI-side mapping — the server `code` flows through the response envelope via `CliResponseReader`.

Note: `watch` is an overloaded term in this CLI (`mo run watch` = poll a run). Nesting under
`issue` disambiguates it for the parser; documented for reviewers.

## Risks / Trade-offs

- **[RoutingDispatchHandler's responsibility broadens]** → Mitigation: restate its role as
  event→agent dispatch via rules *and* watches; isolate watch logic in named private helpers;
  keep the method body readable as one ordered flow.
- **[Removing the `rules.Count == 0` early return changes hot-path behavior]** → Mitigation:
  the watch pass is itself a no-op when there are no watching entries; guard it on
  `evt.Type` ∈ fixed set *and* issue presence so the common case (no issue / other event type)
  exits early. Verified by spec scenario "Event without issue does not trigger watch".
- **[Mute with no covering rule is a runtime no-op]** → Accepted and documented (D3); cheaper
  and more robust than coupling the command to rule evaluation.
- **[Watch launches on `run.failed` may be high-volume]** → Mitigation: fixed event set is the
  contract; grain first-writer + handler dedup protect against replay storms. Throttling is out
  of scope.
- **[Reusing `TriggerRuleId` conflates rule-id and watch-source]** → Mitigation: `watch:`
  prefix makes the source distinguishable; dedicated label deferred (D8).
- **[Cross-context read in `EnrichAsync`]** → No new coupling; single shared `DbContext`
  already spans contexts for every projected relation.

## Migration Plan

Additive only — no data backfill, no breaking changes, no public API removals.

1. **Server / persistence:** add `WatchEntryRow` + DbSet + EF config + migration
   `<ts>_AddIssueWatchEntries.cs` (mirror `20260718100000_AddRoutingRules.cs`). Forward-only
   by convention; rollback = revert the migration.
2. **Server / Agent context:** `WatchEntry`, `WatchEntryState`, `WatchEntryStore` (with
   active-Agent validation + `WatchEntryValidationException`).
3. **Server / dispatch:** muted suppression + watch-launch pass in `RoutingDispatchHandler`;
   built-in watch prompt; remove the no-rules early return.
4. **Server / read model:** `Watching`/`Muted` on `IssueReadModel` + projection block in
   `IssueQuerier.EnrichAsync`.
5. **Server / API:** `IssueRoutes.Watch.cs` (POST add, DELETE remove), mounted under
   `IssueRoutes`.
6. **CLI:** `MohistCliCommands.Issue.Watch.cs`; promote `ResolveAgentAsync`/`AgentRef` to
   shared; `IssueShow` rendering gains watching/muted sections in `TableRenderer.Issues.cs`.
7. **Web:** add `watching`/`muted` to the `Issue` TS interface and two read-only
   `CollapsibleRailCard` entries in `IssueDetailPage.tsx`'s reference rail.

**Deploy order:** server first (migration is additive; dispatch tolerates absence — no
WatchEntry rows means no watching/muted behavior), then CLI, then Web. Web is read-only and
tolerates a temporarily-absent field (it just renders no card). **Rollback:** revert code +
drop the table; nothing depends on WatchEntry existing.

## Open Questions

- **`watch list` output shape:** reuse `IssueShow` rendering (consistent with `add`/`remove`)
  or give `list` a compact dedicated renderer / `TableShape`? Lean: reuse `IssueShow` for
  `add`/`remove`; decide `list` during implementation.
- **Dedicated `TriggerSourceKind` label:** defer unless downstream querying needs to
  distinguish watch vs routing launches cleanly (D8). Track as a follow-up if a query use case
  appears.
- **`watch remove` intent surfacing:** the contract reports resulting state, which encodes
  whether a mute was created or a watch deleted. Confirm whether a one-line confirmation
  ("muted"/"unwatched") is wanted for clarity; current lean is state-as-truth only.
