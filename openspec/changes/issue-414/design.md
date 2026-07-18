## Context

Issue-414 replaces the priority-arbitrated Agent subscription model with a project-level ordered **routing table** whose evaluation is shared with a read-only dry-run. The contract (why) is in [`proposal.md`](proposal.md); the required behavior is in [`specs/routing-rules`](specs/routing-rules/spec.md), [`specs/routing-dispatch`](specs/routing-dispatch/spec.md), [`specs/routing-dry-run`](specs/routing-dry-run/spec.md). This document covers how.

Current state of the code being replaced:

- `Agent/Domain/AgentSubscription.cs` — three-field `SubscriptionFilter` (`Type` glob + `Source`/`Subject` exact) + nullable `Priority`.
- `Agent/Services/AgentSubscriptionStore.cs` — project-scoped CRUD with `active`/`archived` lifecycle and a per-Agent unique name.
- `Events/Subscriptions/AgentSubscriptionDispatchHandler.cs` — `[Subscription(Type = "*")]` bus handler; resolves project id from the envelope, filters active subscriptions, then `Arbitrate` picks one winner by `Priority`.
- `Agent/Services/AgentLauncher.cs` — `BuildTriggerIdentity` builds the idempotency key from `projectId\neventId\nsubscriptionId`; `StableId` hashes it into the `AgentSession` id and the `AgentJob` grain key, giving same-event×subscription de-dup.
- `Sessions/Services/GenericAgentSessionMetadata.cs` — label keys `mohist.io/trigger/event-id` and `mohist.io/trigger/subscription-id`; `AgentSessionQuery` filters sessions on the matching stored computed columns (`LabelTriggerEventId`, `LabelTriggerSubscriptionId`).
- `Events/Subscriptions/ResponsePromptRenderer.cs` — three hardcoded tokens (`{{workflow_run_id}}`, `{{stage}}`, `{{event_type}}`); missing attribute ⇒ empty string.

Foundations already landed by the prerequisites:

- Issue-412: canonical event lineage stamped on every envelope (`projectid`, `issue`, `epic`, `workflowrunid`, `stage`, …).
- Issue-413: `EventMatchExpression.Compile(source)` / `.Matches(EventMatchInput)` — CEL-subset matcher, bounded regex timeout, runtime errors ⇒ `false`, write-time diagnostics. `CloudEventEventMatchInput` adapts a `CloudEvent` to `EventMatchInput` (core fields + extensions, `event.data` unresolvable).

Constraints / boundaries (from [`design/architecture.md`](../../design/architecture.md), [`design/event-routing.md`](../../design/event-routing.md), [`design/event-protocol.md`](../../design/event-protocol.md)):

- Routing lives in the **Agent context**, consumes the CloudEvent infra layer, and MUST NOT reference `Workflow.Domain` or `Issue.Domain`. Matching and rendering are **envelope-only** — no cross-aggregate reverse-query.
- Agent responses go through the regular command surface; there is no special approval channel.
- Dry-run and real dispatch MUST use one evaluation path; "symmetry is an acceptance criterion".
- No version compatibility is owed (active development), so removals carry no compatibility layer.

## Goals / Non-Goals

**Goals:**

- Replace subscription Filter+Priority with an ordered `RoutingRule` table (match expression, response Agent, response prompt, `continue` flag, project-scoped Position).
- One `RoutingTableEvaluator` consumed by both the dispatch handler (launches) and the dry-run (prints), so conclusions cannot diverge.
- `{{event.<attr>}}` rendering over the same namespace as the match expression, with legacy token aliases.
- `mo routing rule create/list/show/update/archive/move` and `mo routing test [--last N]`.
- Idempotent launch keyed by (project, event, rule); bidirectional event↔rule↔AgentJob lookup.
- Delete the old subscription model, API, CLI, and arbitration path in the same change.

**Non-Goals** (from proposal / [`design/event-routing.md`](../../design/event-routing.md) "Not doing"):

- No data migration of existing subscriptions; no compatibility layer.
- No Agent-specific approval channel.
- No rule-conflict strong-validation (visibility + dry-run replace it).
- No per-rule retry/outbox, no per-Agent concurrency gate, no trigger frequency limiting / cooldown.
- No `event.data.*` matching; no dry-run of fabricated events (only real replay).
- Web rule-management UI is out of scope (follow-up).

## Decisions

### D1 — Routing rule model and dense-integer Position

`RoutingRule` carries `Id`, `ProjectId`, `Name`, `Position` (int), `Match` (expression text), `AgentId`, `ResponsePrompt`, `Continue` (bool), `Status` (`active`|`archived`), `CreatedAt`, `UpdatedAt`. One row per rule in a new `RoutingRules` table. Indexes: `UX_RoutingRules_ProjectId_Name` (unique, project-unique names), `IX_RoutingRules_ProjectId_Position` (ordered dispatch/list reads), `IX_RoutingRules_ProjectId`.

`Position` is **dense**: after every create/move the affected project's rules are loaded in order and re-stamped with `1..n` inside one transaction. The spec literally requires "a single strict ascending sequence with no gaps and no ties"; dense renumbering makes that invariant trivially true and trivially testable.

- *Alternatives considered:*
  - **Gap-based positions** (insert mid-list by halving a gap between neighbors). Rejected: routing tables are small (tens of rules) and rarely mutated, so gap maintenance, overflow handling, and tie-breaking add complexity for no win. Dense renumber is O(rows in one project) per mutation, which is negligible.
  - **Linked-list (prev/next pointers).** Rejected: ordered reads and "strict sequence" invariants are harder to verify than a dense int.

### D2 — One shared `RoutingTableEvaluator`

A pure service evaluates one event against a project's ordered active rules and returns an ordered trace:

```
record RuleOutcome(
    RoutingRule Rule,
    RuleMatchResult Match,          // NotMatched | Matched
    RuleExecutable Executable,      // WouldLaunch | SkippedInactiveAgent | SkippedEmptyPrompt | SkippedRuntimeError
    string? ResolvedAgentName,      // for dry-run "would trigger"
    string? RenderedPromptPreview); // for dry-run visibility (dispatch uses full render)
```

Matching delegates to `EventMatchExpression.Compile(rule.Match).Matches(input)` — runtime errors already return `false` inside `Matches`; the evaluator classifies that as `NotMatched + SkippedRuntimeError` (detected via a recording `IEventMatchFailureSink`, the extension point issue-413 already provides). Executability is resolved through an `IRuleExecutionProbe` (Agent active? rendered prompt non-empty?) so dispatch and dry-run pass **the same probe** and therefore agree on "matched but not executable".

- Real dispatch: iterate outcomes in order; for each `Matched + WouldLaunch`, call `IAgentLauncher.LaunchAsync` with trigger labels; **stop after the first `Matched` whose `Continue == false`**. `Skipped*` outcomes do not stop evaluation (they are non-matches with a structured log).
- Dry-run: iterate the same outcomes and render them; never call the launcher.

This is the single point that guarantees the "shared semantics" spec requirement: there is no second evaluator to drift.

- *Alternatives considered:*
  - **Two callers, shared helper methods.** Rejected: a shared free function is easy to bypass; the dispatch path might re-check executability differently. A single `RoutingTableEvaluator` returning a full trace is the structural guarantee.
  - **Evaluator launches directly.** Rejected: that forces the dry-run to construct a no-op launcher and risks side effects. The evaluator is pure; only the dispatch wrapper launches.

### D3 — Compiled-expression caching

`EventMatchExpression.Compile` is called on every rule on every evaluation. Dispatch and dry-run both benefit from compiling once. Add a small `IRuleExpressionCache` (project-scoped, invalidated on rule mutation in the same process) keyed by `ruleId + rule.Match` hash. The cache is an optimization only — a cache miss recompiles; correctness does not depend on it. Write-time validation always compiles once up front (D5), so a stored rule is always compilable.

### D4 — Dispatch handler replacement

Delete `AgentSubscriptionDispatchHandler` and its `Arbitrate`. New `RoutingDispatchHandler : ICloudEventHandler` with `[Subscription(Type = "*")]`, mirroring the existing per-event `IServiceScopeFactory` scope pattern (same as `InboxProjectionHandler`). Per event it: resolves `projectid` from the envelope (unchanged — skip if absent), loads active rules ordered by Position, compiles+caches, runs `RoutingTableEvaluator`, and launches for `WouldLaunch` outcomes until stop.

Trigger labels become `{ TriggerEventId, TriggerRuleId }`. `AgentLauncher.BuildTriggerIdentity` requires both and derives the same `project\nevent\nrule` hash → `(project, event, rule)` idempotency. `GenericAgentSessionMetadata.TriggerSubscriptionId` is renamed to `TriggerRuleId` with label key `mohist.io/trigger/rule-id`. The session-side stored computed column `LabelTriggerSubscriptionId` is renamed to `LabelTriggerRuleId`, and `AgentSessionQuery`'s trigger filter follows the constant rename.

- *Alternative:* keep the old `subscription-id` label key and reuse it for rule ids. Rejected: the label name is the contract; reusing a misnamed key leaks the old model forever. No version compat is owed, so rename.

### D5 — Write-time validation surface

Validation lives at the API/store boundary (create and update), before persistence, and the stored rule is guaranteed compilable:

1. `EventMatchExpression.Compile(rule.Match)` must succeed; on failure, return the `MatchDiagnostic` (location, message) to the client (reusing the `events tail` compile-failure rendering).
2. `AgentQuerier.GetByIdAsync(projectId, agentId)` must return an Agent whose `Status == Active`; else reject (`agent_not_found` / `agent_archived`).
3. `ResponsePrompt` must be non-blank.

A rejected create stores nothing; a rejected update leaves fields and Position unchanged. Runtime dispatch performs no validation — Agent-later-archived is a `SkippedInactiveAgent` outcome (D2).

### D6 — `{{event.*}}` rendering with legacy aliases

Replace `ResponsePromptRenderer`'s three hardcoded tokens with a token scan: find every `{{event.<ident>}}`, resolve via the same `EventMatchInput` used for matching; **present attribute (including empty) → substitute value; absent → leave verbatim**. The three legacy literals `{{workflow_run_id}}`, `{{stage}}`, `{{event_type}}` are kept as aliases of `{{event.workflowrunid}}`, `{{event.stage}}`, `{{event.type}}`. The renderer takes `EventMatchInput` (not `CloudEvent`) so dispatch and dry-run share it identically.

- *Alternatives considered:*
  - **Full template engine (Liquid/Razor).** Rejected by [`design/event-protocol.md`](../../design/event-protocol.md): no template engine; envelope-only substitution.
  - **Substitute missing with empty string (current behavior).** Rejected by the proposal: unmatched placeholders must stay visible so misconfiguration surfaces in the prompt rather than vanishing.

### D7 — Dry-run event source: dedicated real-event reader

The dry-run must replay **real dispatched events**, not the Web Activity feed. `ProjectEventFeedAssembler` is rejected as the source because it synthesizes `coder_session_started` / `session.closed` entries from session/transcript rows (not real envelopes) and projects a Web-facing shape. Add a `ProjectRecentEventReader` (scoped) that unions the real per-aggregate event tables (`IssueEvents`, `WorkflowRunEvents`, `EpicEvents`, `AgentSessionEvents`) filtered by `projectid`, orders by time desc, takes `N`, and returns each row's canonical envelope as an `EventMatchInput`. This reads the same persisted canonical envelopes the bus dispatches, which is the basis of the "same attributes ⇒ same matcher result" fidelity guarantee.

`mo routing test --last N` → `GET /api/projects/{project}/routing/test?last=N` returns a single JSON document of per-event traces (bounded: `N × rules` outcomes, small). Default `N` when `--last` is omitted is fixed and nonzero (see Open Questions).

- *Alternatives considered:*
  - **Reuse `ProjectEventFeedAssembler`.** Rejected: synthetic entries would make dry-run report hits that real dispatch can never produce, breaking the symmetry guarantee.
  - **NDJSON streaming like `events tail`.** Rejected: the result is bounded and small; a single JSON document is simpler for the CLI to render as a table.

### D8 — CLI surface and removal

New top-level `routing` command (resource-first per [`design/cli.md`](../../design/cli.md)): `mo routing rule create/list/show/update/archive/move` (create/move accept `--before`/`--after`; all project-scoped via active project / `--project` / `--project-id`) and `mo routing test [--last N]`. Delete the `agent subscription` subtree from `MohistCliCommands.Agent.cs` and its spec.

Removal scope (all in this change, no compat layer):

- Server: `AgentSubscription`, `AgentSubscriptionStore`, `AgentSubscriptionQuerier`, `AgentSubscriptionDto`, `AgentSubscriptionRoutes`, `AgentSubscriptionRow`, the `AgentSubscriptions` DbSet + mapping, `AgentSubscriptionDispatchHandler` (+ `Arbitrate`).
- Session metadata: rename `TriggerSubscriptionId` → `TriggerRuleId` and its computed column.
- CLI: `mo agent subscription …`, `CliAgentSubscriptionCommandSpecs.cs`.
- New: `RoutingRule`/store/querier, `RoutingTableEvaluator`, `RoutingDispatchHandler`, `ProjectRecentEventReader`, extended `ResponsePromptRenderer`, `RoutingRulesRoutes`, `RoutingTestRoutes`, CLI `routing` command tree.

### D9 — Testing approach (per [`design/testing.md`](../../design/testing.md))

- **Unit (<50ms):**
  - `RoutingTableEvaluator` — ordered first-match-stops, `continue` fanout, `SkippedInactiveAgent`/`SkippedEmptyPrompt`/`SkippedRuntimeError` as non-matches-with-continue, determinism. Uses an in-memory `RoutingRule` list and a fake `IRuleExecutionProbe`.
  - Dense-Position renumber on create/move (incl. `--before`/`--after` and append).
  - `ResponsePromptRenderer` — `{{event.*}}` substitution, present-empty vs absent, legacy aliases.
  - Write-time validation rejection matrix (compile fail / agent missing / agent archived / blank prompt) and "rejected update leaves rule unchanged".
- **Spec (<500ms):**
  - Dispatch end-to-end through the bus fixture (reuse `DispatcherFixture` + `RecordingAgentLauncher` from the existing subscription dispatch specs, swapped to the new handler): targeted-above-fallback, fanout via `continue`, same-event×rule de-dup, runtime-error-keeps-going, bidirectional event↔rule↔AgentJob lookup via trigger labels.
  - Dry-run spec: replay returns the same hits as a real dispatch for the identical table+events; no `IAgentLauncher.LaunchAsync` calls; empty-state messaging (no rules / no events).
  - CLI spec: `mo routing rule` CRUD/move ordering and `mo routing test` output; `mo agent subscription` no longer resolves.

No real external dependency, no wall-clock (inject `TimeProvider` as the existing store already does).

## Risks / Trade-offs

- **Replacing the live dispatch path** → Mitigation: one shared `RoutingTableEvaluator` makes divergence impossible; focused dispatch spec (targeted/fanout/idempotency/visibility) runs before the full suite.
- **No data migration; existing subscriptions vanish** → Mitigation: documented in `docs/event-routing.md`; project is pre-release. Operators re-author mechanically (`event.type == "…" && event.source == "…"`, Priority desc ⇒ table order).
- **Dense Position renumber writes all project rules per mutation** → Mitigation: tables are tiny and rarely mutated; one transaction. Acceptable; revisit if tables ever grow.
- **Trigger-label rename orphans historical session labels** → Mitigation: active development, no version compat; lookup works for sessions created after the change. Documented as a known asymmetry.
- **Dry-run reads persisted envelopes while dispatch processes in-flight ones** → Mitigation: both derive from the canonical CloudEvent; the persisted row is the canonical envelope; the matcher is a pure function of those attributes, so results coincide. A spec asserts dry-run == dispatch on identical input.
- **Synthetic feed entries would poison dry-run if reused** → Mitigation: dedicated `ProjectRecentEventReader` over real event tables only (D7).
- **Expression cache staleness after a mutation in another process** → Mitigation: cache is a correctness-irrelevant optimization; a miss recompiles. In single-process deployments the cache is invalidated on mutation.

## Migration Plan

Single EF Core migration in this change:

1. `DropTable("AgentSubscriptions")`.
2. `CreateTable("RoutingRules")` with D1 indexes/columns.
3. Rename the `AgentSessions` computed column `LabelTriggerSubscriptionId` → `LabelTriggerRuleId` (re-derived from the `mohist.io/trigger/rule-id` label key).

Deploy: apply migration; old subscription data is gone (no migration). Operators re-author routing rules. Rollback: revert code + a counter-migration (`drop RoutingRules`, `recreate AgentSubscriptions`, rename the column back). Rules authored after the change are lost on rollback — acceptable given pre-release status.

## Open Questions

- **Default `--last N`** for `mo routing test`. Propose **20** (small enough to scan, large enough to be useful). Pin in the CLI help and the dry-run spec scenario.
- **`mo routing rule list` archived visibility.** Propose listing archived rules too (with status), since archive is non-destructive and there is no restore command; confirm in implementation.
- **Restore command.** Proposal scope is `archive` only (terminal). Defer `restore` to a follow-up if operators ask; not blocking.
- **`RoutingRules` `Position` type.** `int` is sufficient for dense per-project positions; confirm no project is expected to exceed `int` (not a real concern).
