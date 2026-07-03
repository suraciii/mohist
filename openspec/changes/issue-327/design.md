## Context

`AgentSessionQuerier` (`Sessions/Services/AgentSessionQuerier.cs`, 1635 lines) is the single read-side entry point for the Session domain. It currently fuses seven orthogonal concerns behind one `IScopedService`: workflow/generic session listing & detail, followup/cancel target resolution, activity-feed assembly (`GetActivityAsync` + its card/preview/task-progress helpers), usage/cost reporting (`GetUsageTimeseriesAsync` / `GetCostRollupAsync` / `GetCostWindowedAsync` + their aggregation helpers), lineage projection, and run-session reconciliation. Navigating "which block serves the activity endpoint" is impractical.

Layered on top are three independent cleanliness defects, each verifiable by search:

- **Dead code** — `ToAgentSessionDto` (`AgentSessionQuerier.cs:1274`) and its return type `AgentSessionDto` (`AgentSessionReadModels.cs:40`) have zero callers anywhere in the codebase.
- **Copy-paste** — the transcript load sequence (`turns → turnIds → parts → sessionByTurnId` dictionary) is duplicated across five sites (`LoadLatestEventsAsync`, `LoadEventSummariesAsync`, `LoadTerminalFactsAsync`, `GetGenericSessionSummaryAsync`, `BuildSessionMetadataDtoAsync`). Two context-reference envelope builders (`BuildAgentSessionListContextRefs` / `BuildGenericSessionSummaryContextRefs`) are character-for-character identical except for their return DTO.
- **Vocabulary drift** — closure events are emitted as `session.closed` (`RuntimeEventTypes.SessionClosed`) but persisted/transcribed as `session_closed` (`TranscriptPartTypes.SessionClosed`). Readers therefore hardcode `p.Type == "session_closed" || p.Type == "session.closed"` (`ReadTerminalStateAsync:445`) and several literals (`SessionTranscriptBuilder.cs:77`, `AgentSessionSummaryBuilder.cs:79,114`).

Issue #330 already relocated Session read-side code physically under `Sessions/`, so the directory prerequisite is satisfied and internal decomposition is now safe. All three changes are pure refactor: no DTO wire shape, HTTP contract, status-resolution, ordering, or nullability changes.

**Stakeholders / boundaries** — this is entirely within the Session bounded context (read side). No cross-domain contract changes (`design/architecture.md`: execution-fact vs state-adjudication separation is untouched — read models stay in server). DI is auto-wired via the `IScopedService` marker scan (`ServiceCollectionExtensions`), and `MigratedServicesRegistrationSpecs` asserts each scoped service registers as-self — new services must be added there.

## Goals / Non-Goals

**Goals:**
- Usage/cost reporting and activity-feed assembly each become independently navigable services, separate from the core query class.
- Delete the zero-call `ToAgentSessionDto` / `AgentSessionDto` dead code.
- Transcript turns/parts loading defined in exactly one place; all five former call sites delegate to it.
- The two identical context-ref builders collapse to one shared construction site (both wire DTOs remain distinct).
- Closure transcript part type is the single token `session.closed` end-to-end (emit → accumulate → persist → every reader); no `session_closed` constant or dual-spelling matcher remains.
- Every byte-for-byte observable response preserved; all existing session specs green.

**Non-Goals:**
- No change to the transcript storage model (turns/parts tables).
- No cleanup of the lineage fallback synthesis (historical-data compatibility — separate evaluation).
- No `cancelled`→`stopped` global alias normalization (cross-cutting product decision).
- No change to session label key string values or any external API contract.
- followup/cancel target resolution stays in the core query class (acknowledged smell, out of AC scope).

## Decisions

### D1 — Decompose into two new scoped services, not partial-class method groups
Extract `AgentUsageReporter` (usage timeseries + cost rollup + cost windowed + their private aggregation helpers: pre-window spend, cumulative-cost-per-ship, completed-issue-count load, per-window figure build, `UsageBucketData`, `HasUsage`) and `AgentActivityFeedAssembler` (`GetActivityAsync` + `ToActivityCard`, `BuildTaskProgressMapAsync`, `LoadIssueTitlesAsync`, preview/truncate helpers) into separate `sealed` classes under `Sessions/Services/`, each implementing `IScopedService`. The core `AgentSessionQuerier` keeps listing/detail/metadata/transcript/followup/cancel/terminal-state/reconciliation.

**Rationale** — the entire motivation is per-concern navigability; partial-class method groups keep everything in one file and defeat the goal. Separate classes match the existing convention (`WorkflowQuerier`, `IssueQuerier`, `WorkflowActivityQuerier` are each one-concern scoped services). `IScopedService` auto-registration means no manual `AddScoped` wiring.

**Alternatives considered** — (a) partial class: rejected (same file, no navigability win). (b) Move only one concern: rejected (both are independently large and serve distinct routes). (c) Introduce a shared `ISessionReadService` interface façade: rejected (adds indirection with no consumer benefit; routes already depend on concrete queriers in this codebase).

### D2 — Dependency direction is one-way: extracted services → core querier, never back
`AgentActivityFeedAssembler` needs session listing + active-session reconciliation, both currently private to the querier (`_sessionQuery.ListByLabelsAsync`, `ReconcileActiveSessionsAsync`, `LoadLatestEventsAsync`/`LoadEventSummariesAsync`). The assembler depends on the core querier (and the shared loader, D3); the core querier does **not** depend on either extracted service. This keeps the dependency graph acyclic.

**Rationale / consequence** — `ReconcileActiveSessionsAsync` is consumed by both the core querier (`ListCurrentAsync`) and the activity assembler, so it stays on the core querier as an `internal` method the assembler calls, rather than moving. As a side effect the core querier constructor drops its `_workflowQuerier` parameter (only `BuildTaskProgressMapAsync`, now in the assembler, used it) — the assembler takes `_workflowQuerier` instead.

**Alternatives** — (a) hoist `ReconcileActiveSessionsAsync` into a third shared helper: rejected (only two consumers, both already coupled to the querier). (b) Keep `_workflowQuerier` on the core querier unused: rejected (compiler/treat-warnings hygiene; dead ctor arg).

### D3 — One internal static transcript loader returns raw materials; callers impose ordering/last-wins
A single `internal static` loader (e.g. `TranscriptPartLoader.LoadAsync(db, sessionIds, partTypeFilter?)`) returns the `sessionByTurnId` map plus the materialized parts (optionally pre-filtered by part type so terminal-fact loading stays SQL-filtered). It does **not** impose ordering or per-session reduction — each of the five call sites applies its own existing ordering key (`LastSeenAt,Id` for latest-event; `Sequence` for summaries; `Sequence,Id` for terminal facts) and last-wins semantics in LINQ-to-Objects. `LoadTranscriptAsync` (single-session, returns turns+parts for `SessionTranscriptBuilder`) becomes a thin wrapper delegating to the same loader.

**Rationale** — the five sites differ only in ordering/filter/reduction; the genuinely duplicated core is the three-query sequence + dictionary. Returning materials preserves byte-identical results (same rows, same final order imposed by the caller) while collapsing to one definition. Static + `db`-passed matches the existing `TerminalFact` / `TranscriptEventSummaryProjector` static-projector convention and avoids instance coupling.

**Alternatives** — (a) loader returns already-reduced per-session dictionaries: rejected (would force five different reduction signatures, re-introducing duplication). (b) Loader returns an `IQueryable`: rejected (EF ordering/translation differs per caller; materializing once is simpler and sessions are bounded).

### D4 — Context-ref builders merge into one shared parser returning a nullable value tuple
A single `internal static` helper (e.g. `AgentSessionContextRefs.TryBuild(record)`) reads the four launch labels (`IssueNumber`, `EpicNumber`, `Repository`, `WorkspacePath`), parses the issue number, and returns `(int? IssueNumber, string? EpicNumber, string? Repository, string? WorkspacePath)?` — `null` when all four are absent. Each caller maps the non-null tuple to its own DTO (`AgentSessionListContextRefsDto` / `GenericAgentSessionSummaryContextRefsDto`). Both DTOs remain distinct wire shapes.

**Rationale** — the construction *logic* is identical; the *output types* are intentionally distinct (different consumers). Sharing logic while keeping types separate removes the duplication without coupling two unrelated envelopes.

**Alternatives** — (a) collapse the two DTOs into one: rejected (spec mandates both wire shapes persist; they serve different routes). (b) Generic `Build<T>(record, Func<...> ctor)`: rejected (over-engineered for a four-field tuple).

### D5 — Single dot-token `session.closed`; accept the write-side value change under "no version compat"
Set `TranscriptPartTypes.SessionClosed = "session.closed"` (remove the underscore variant entirely). `TranscriptAccumulator.ToTranscriptPartType` then persists `session.closed` for new closure parts. Every reader references the single constant; the `|| "session_closed"` branch (`ReadTerminalStateAsync:445`) and the literal `"session_closed"` comparisons (`SessionTranscriptBuilder`, `AgentSessionSummaryBuilder`, `TerminalFact`, `TranscriptEventSummaryProjector`) are replaced by the constant.

**Rationale** — `RuntimeEventTypes.SessionClosed` is already `session.closed` and the runner emits dot-format, so the underscore constant is the lone outlier; unifying write+read removes the dual-judge hack. Per `AGENTS.md` ("本项目正处在积极开发过程中，无需考虑版本兼容"), the fact that *existing persisted rows* carry `session_closed` is an accepted non-issue for this local-first dev system — a dev DB reset clears them.

**Alternatives** — (a) keep dual-spelling on read for backward compat: rejected (directly contradicts the AC and re-entrenches the smell). (b) Backfill existing rows to `session.closed` via a migration: rejected as default (no schema change is in scope; add only if a deployed instance with real history exists — see Open Questions).

## Risks / Trade-offs

- `[Historical session_closed rows lose terminal-state resolution after the read side drops the underscore match]` → Accepted under the project's no-version-compat stance; document in the change note. If a deployed instance matters, ship a one-shot `UPDATE ... SET Type='session.closed' WHERE Type='session_closed'` backfill (no schema change) before merge.
- `[Missed literal "session_closed" comparison site]` → Mitigation: post-edit `rg '"session_closed"'` over `Sessions/` must return zero type-comparison hits; `TreatWarningsAsErrors` + the literal-asserting specs (updated to `session.closed`) close the loop.
- `[Core querier constructor signature change (drops _workflowQuerier) breaks direct-construction unit specs]` → Mitigation: one direct-construction site (`GenericAgentSessionSummarySpecs.cs:210`, already passing `null!` for that arg); update it, or switch it to DI resolution.
- `[New service not registered / wrong lifetime]` → Mitigation: add a row per new service to `MigratedServicesRegistrationSpecs.MigratedServices` (Scoped); the theory asserts self-registration with correct lifetime.
- `[Activity-assembler ↔ core-querier circular dependency]` → Mitigation: enforced one-way dependency (D2); compiler fails fast on any reverse reference.
- `[Large diff obscures a behavior change]` → Mitigation: stage in the order below so each step compiles + stays spec-green independently; the decomposition steps are pure relocation.

## Migration Plan

Stage as independent compile+spec-green commits (no schema migration, no API contract change, single PR):

1. **Event-naming unification** — flip `TranscriptPartTypes.SessionClosed` to `session.closed`, remove underscore constant, update accumulator + all readers to the constant, update literal-asserting specs (`AgentSessionSpecs`, `AgentSessionReadApiSpecs`, `GenericAgentSessionSummarySpecs`, `AgentJobGrainSpecs`) to `session.closed`. Isolated, lowest blast radius — do first.
2. **Shared assembly helpers** — introduce the single transcript loader (D3) and context-ref parser (D4); rewire the five load sites and both context-ref callers onto them. Pure relocation.
3. **Extract `AgentActivityFeedAssembler`** — move activity methods + helpers, drop `_workflowQuerier` from the core querier ctor, update the `/activity` route DI and the direct-construction spec.
4. **Extract `AgentUsageReporter`** — move usage/cost methods + aggregation helpers, update `/usage` and `/cost` routes, add registration-spec rows for both new services.
5. **Delete dead code** — remove `ToAgentSessionDto` + `AgentSessionDto`; compiler confirms zero references.

**Rollback** — pure refactor on a single PR; `git revert` restores prior state. The event-name change is forward-only on *writes* (new rows are `session.closed`); revert restores the dual-spelling reader so any post-merge dev rows are re-matched. No data/rollback script required for the dev workflow.

## Open Questions

- **Service naming** — propose `AgentUsageReporter` and `AgentActivityFeedAssembler`; confirm final names during implementation (existing convention favors `*Querier`/`*Service`, but these are report/feed *assemblers* rather than pure queries).
- **Shared DTO mappers** — `ToUsageDto`/`BuildUsageHistoryDto` are referenced by both the usage reporter (after extraction) and the generic-session summary (core querier) + recovery specs. Decide at implementation time whether they stay as `internal static` on the core querier (assembler/reporter call through) or move to a small shared mapper; either preserves behavior.
- **Deployed-instance backfill** — if any non-dev instance carries historical `session_closed` parts that must keep resolving terminal state, ship the `UPDATE` backfill noted in Risks before merge. Default is no backfill (dev-only).
