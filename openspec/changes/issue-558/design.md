## Context

Today the Web cannot explain what an Agent execution produced:

- **Agent page history** (`packages/web/src/pages/agent-detail/ui/AgentDetailPage.tsx`) groups sessions by Activity: `active` → "Running", `unknown` → "Failed", `idle` → "Ended", plus an overlapping "Recent" slice. Each row shows a status dot, the Agent name (redundant — every row belongs to that Agent), the model, and a timestamp. No task, no context references, no result.
- **Server list read** (`AgentSessionQuerier.ListAgentSessionsAsync` / `ListUnifiedSessionsByAgentAsync`) returns `AgentSessionListItemDto` / `UnifiedSessionListItemDto` with Activity, timestamps, model, context refs, origin — but no first-input subject and no Turn result facts. It already loads full transcripts per session (`TranscriptReductions.LoadEventSummariesAsync`) just for the resolved model.
- **Session page** (`pages/session/ui/SessionDetailShell.tsx`) surfaces the launch result only when the URL carries `?jobId=` (fresh-launch flow using the existing `launchObservationQueryOptions`); a session opened from history has no launch-result presentation. A completed Turn renders as a muted `status` row (`turnStateFacts` in `widgets/session-transcript/model/timeline-facts.ts` maps `completed` → kind `status`), while `failed`/`cancelled` map to `error`.
- **Docs** (`docs/web-ui.md`) still list "AgentJob has no result view separate from its continuing AgentSession" as an Agents implementation gap; `design/session-timeline.md` has no result-entry semantics.

The facts this change needs are already recorded and already loaded:

- `AgentSessionQuery` deserializes the full `AgentSession` domain record per row. `Status.Inputs[0].Text` is the first input; `Status.Turns` carries every `AgentTurnRecord` with `Status` (`queued`/`executing`/`completed`/`failed`/`cancelled`/`unknown`) and `Result` (`Message`, `Output`, `FailureReason`, `FailureCategory`, `ExitCode`); `AgentTurnRecord.JobId` links the launch turn to its AgentJob.
- `Status.UsageSummary` on the same snapshot carries the session's accumulated cost (`CostAmount`/`CostCurrency`) and token totals, and the lifecycle anchors for start/end/duration (`CreatedAt`, `LastDataAt`, `CurrentTurnEndedAt`, `IdleSince`, `BoundAt`); each `AgentTurnRecord` additionally records `RecordedAt`/`UpdatedAt`, so Turn-level elapsed is a recorded fact too.
- `UnifiedSessionSummaryDto.Turns` already exposes all Turn observations (via `AgentSessionObservationMapper.Turns`) to the Session page.
- The AgentJob read surface (`/agent-jobs/{jobId}/launch-observation`, `AgentLaunchObservationAssembler`) already composes Job + first-Turn result facts for the fresh-launch flow.

Constraints: read-and-presentation layer only — no new persisted session state, no new events, no new transcript fact types, no change to Session state authority (see proposal and `specs/`). Product language: Activity is not a result; `unknown` Activity is never "Failed"; a failed Job is not a failed Session.

## Goals / Non-Goals

**Goals:**

- Agent-scoped and agent-filtered unified session list reads expose, per session: a bounded first-input subject excerpt, the latest AgentTurn's terminal result facts in result vocabulary (`completed` / `failed` / `cancelled` / `unresolved`), the first AgentJob (launch) result supplied by the first JobId-bearing Turn, the session's accumulated cost, and its derived end time (elapsed computed from the recorded lifecycle anchors).
- The Agent page history renders result-bearing rows (subject, origin, context references, start/end/elapsed and cost, Activity as a separate signal) grouped by execution outcome, never by Activity.
- The Session view's first viewport surfaces the most-recent result distinct from the Activity badge, and presents the launch result for launch-origin sessions without letting later Turns rewrite it.
- The Session timeline renders terminal Turn results as first-class, sentence-form outcome entries with expandable structured evidence layered on the same recorded facts as the raw view.
- Documentation converges: the AgentJob result-view gap closes, the AgentSession gap footnote no longer lists the removed gaps, and `design/session-timeline.md` defines result-entry semantics.

**Non-Goals:**

- No new persisted state, events, transcript fact types, or changes to the event protocol / external session stream (`agent-api.md` projection is untouched).
- No outcome-based query filtering or server-side grouping of list endpoints (grouping is a Web presentation concern).
- No rewrite of the full sentence-form timeline model from `design/session-timeline.md` (domain-action recognition, salience policy for routine items) — only the terminal-result entry layer this change needs.
- No AgentJob detail page or Job-lifecycle UI; the launch observation remains the Job read surface.
- No workflow-origin session changes beyond what the shared unified DTO already carries.

## Decisions

### D1 — Result facts are derived from the already-loaded status snapshot, not from transcripts, grains, or the AgentJob store

`ListAgentSessionsAsync`, `ListUnifiedSessionsByAgentAsync` (and the workspace variant that shares the unified DTO) already hold the deserialized `AgentSession` per row. The subject excerpt, latest-Turn outcome, and launch outcome are pure functions over `Status.Inputs` / `Status.Turns` computed at the read boundary — zero additional queries, zero grain calls, no AgentJob join.

*Alternatives rejected:*
- *Per-row launch-observation fetch* (Web calls `/agent-jobs/{jobId}/launch-observation` for each history row): N+1 requests against Orleans grains for up to 200 rows on a repeatedly re-fetched list; also requires a jobId the list does not carry.
- *Deriving outcomes from `TranscriptReductions` summaries:* heavier (already-loaded transcripts aside, the vocabulary would come from transcript events, not the authoritative Turn records) and wrong source — the spec derives outcomes from Turn result facts.
- *Deriving from Activity:* explicitly forbidden.

### D2 — One shared outcome envelope; result vocabulary normalized at the server read boundary

Add to `AgentSessionReadModels.cs` one shared record (following the shared `AgentTurnResultObservationDto` precedent, not the per-surface context-ref duplication):

- `AgentSessionTurnOutcomeDto(TurnId, Sequence, Outcome, Result, RecordedAt, UpdatedAt)` where `Outcome` ∈ `completed | failed | cancelled | unresolved`, `Result` reuses the existing `AgentTurnResultObservationDto` shape (message, output, failure category/reason, exit code), and `RecordedAt`/`UpdatedAt` mirror the Turn record's own timestamps — so a Turn's elapsed (`UpdatedAt − RecordedAt` when both recorded) is a recorded fact, giving the launch chip Turn-level duration instead of session-level proxies.
- `AgentSessionListItemDto` and `UnifiedSessionListItemDto` each gain five optional fields appended after the existing optional parameters (`Origin`/`TargetId` precedent, so existing constructor call sites compile): `Subject` (bounded excerpt, `null` when no recorded first-input text — absent rather than fabricated), `LatestTurn` (`AgentSessionTurnOutcomeDto?`), `Launch` (`AgentSessionLaunchOutcomeDto?` = `JobId` + the first JobId-bearing Turn's `AgentSessionTurnOutcomeDto`), `Cost` (`AgentSessionCostDto?` = `CostAmount` + `CostCurrency` from `Status.UsageSummary`, `null` when no cost is recorded — absent rather than zero), and `EndedAt` (the latest non-null lifecycle anchor among `Status.LastDataAt`, `CurrentTurnEndedAt`, `IdleSince`, `BoundAt`; `null` when nothing beyond `CreatedAt` is recorded — the start time is the already-carried `CreatedAt`). Session elapsed is `EndedAt − CreatedAt` computed at presentation; per-execution cost beyond the session-level summary is honestly unavailable because usage accumulates per session, not per Turn.

Normalization happens server-side, at the same read boundary that already normalizes vocabulary (the runner-protocol Activity `cancelled` alias is normalized to `stopped` on these list DTOs — same boundary, a different vocabulary): `completed`/`failed`/`cancelled` map from terminal Turn statuses; no Turn, `queued`, `executing`, and `unknown` all resolve to `unresolved`. Every consumer gets honest vocabulary instead of each client re-deriving it.

### D3 — The launch result is the first AgentTurn that carries a `JobId`

`AgentTurnRecord.JobId` is stamped on the first Turn of every session created by the launch coordinator — this covers **both** launch source classes: direct `agent-launch` sessions **and** `agent-connection` (Slack connection) sessions, which the coordinator also creates with a real AgentJob (`AgentLaunchCoordinatorGrain` stamps `Source = "agent-connection"` and passes `JobId: plan.JobKey` — a live `IAgentJobGrain` id — into `EnsureInitialLaunchAsync`, which requires a JobId and stamps it on the first Turn). Only follow-up Turns never carry one. Derivation rule: the lowest-sequence Turn with a `JobId` supplies the launch outcome and the `JobId` link. The rule is fact-based (Turn-carried JobId), not source-kind-based: both launch classes get a `Launch` envelope on both surfaces, and sessions with no JobId-bearing Turn (no Turn at all, workflow-created sessions, sessions predating the stamp) get none — nothing is fabricated and no recorded fact is suppressed. Because the rule is positional-by-Job rather than "latest", later Turns can never rewrite the presented launch result.

The existing AgentJob read surface stays the deep-read path: the fresh-launch flow (`?jobId=` in URL → `launchObservationQueryOptions`) is unchanged, and the Turn facts remain session-read facts, exactly as the spec states ("supplied by the session's first AgentTurn"). No duplicated AgentJob query path is built.

### D4 — History grouping derives from the latest Turn outcome; launch result and Activity are in-row signals

The Agent page history groups rows as **Failed / Completed / Cancelled / Unresolved** (each recency-ordered by last activity), using only the latest-Turn outcome as the group key:

- A failed launch followed by a completed follow-up Turn lands in **Completed** — the failure stays visible in-row as the launch result ("a failed Job is not a failed Session").
- A session whose only Turn is the failed launch lands in **Failed** because the *Turn* failed — a turn-fact derivation, not a Job-status or Activity inference.
- `unknown` Activity can never reach "Failed" because Activity is not a grouping input; it renders as its own badge per row.

The "Recent" slice that currently duplicates rows across groups is removed; recency becomes ordering within groups.

*Alternatives rejected:* composite two-axis grouping (launch × latest) — combinatorial and unscannable; keeping Activity groups — forbidden by the spec.

### D5 — History rows are task-bearing

Row primary label = `Subject` excerpt (fallback to an honest placeholder only when the excerpt is absent — never the Agent name). Secondary line: origin, context references rendered as links (Issue/Epic numbers resolve to their pages via the existing `toProjectPath` patterns; repository/workspace as text or workspace link; Slack provenance from origin/target when present), model. Trailing signals: start time (`createdAt`), end time (`endedAt`) and elapsed — for a session whose Activity is still active, elapsed-so-far rather than a fabricated end — cost (amount + currency, omitted when unrecorded), Activity badge, launch-result chip (carrying the launch Turn's own `recordedAt`/`updatedAt`, so the first execution's duration is Turn-level), latest-outcome chip. Absent context is omitted, not rendered as empty placeholders.

### D6 — Session header's most-recent result derives client-side from the already-exposed Turn observations

`UnifiedSessionSummaryDto.turns` already carries every Turn with its result. One pure helper in `entities/session` (e.g., `deriveLatestTurnOutcome(turns)`) selects the highest-sequence Turn with a terminal status and maps it to the same result vocabulary; non-terminal resolves to `unresolved`, never inferred from Activity. The header renders it as a distinct signal beside the `StatusBadge`, with the result message (bounded excerpt) or failure category + reason.

*Alternative rejected:* adding a derived `latestTurnOutcome` field to the summary DTO — it would duplicate facts already present in the same payload and add a second place to keep in sync.

### D7 — Launch result on the Session view identifies the launch Turn by `JobId`

Expose `jobId` on `AgentTurnObservationDto` (the fact already exists on `AgentTurnRecord`; the observation mapper just does not map it today). The Session header then presents the first JobId-bearing Turn's terminal result as the **launch result**, labeled as the first execution and visually distinct from the most-recent result. This is more robust than positional "first Turn" and enables linking to the launch-observation surface. Sessions with no JobId-bearing Turn (e.g. workflow-created sessions) show no launch result section; agent-connection sessions do show one — their first execution is an AgentJob launch with a recorded result (D3) — keeping the Session header and the history row consistent for the same session.

### D8 — Timeline terminal Turn results become `outcome` entries

Extend the timeline presentation model (`entities/session/model/timeline`):

- New `TimelineFactKind`/`RenderClass` `outcome`, produced by `turnStateFacts` for terminal Turn results. `completed` → sentence-form summary carrying the result message ("已完成：<excerpt>"), salience `normal` — no longer the muted `status` row. `cancelled` and `unresolved` → stated honestly in sentence form. `failed` keeps today's `error` class and `critical` salience, so it stays a prominent entry that never collapses.
- Outcome entries never enter collapsible groups — grouping is already restricted to `file-read`/`shell`/`tool` terminal items and stays that way; outcome items break consecutive group runs like other non-groupable classes.
- Expandable detail (the existing `<details>` pattern in `TimelineItemRow`) carries the structured evidence: result message, output excerpt, failure category + reason, exit code when recorded, and the inputs the Turn processed (resolved from the input facts correlated by `turnId`). `detail.raw` remains the Turn observation itself, so the raw view and the outcome entry are two presentations of the same recorded facts — no separate result record is introduced.

### D9 — Documentation converges

- `design/session-timeline.md`: add result-entry semantics — the `outcome` RenderClass row in the presentation-class table, sentence forms per outcome, the no-group rule, the failed-stays-error rule, and the expandable evidence contract; update the Status section so turn result presentation is specified.
- `docs/web-ui.md`: delete the "AgentJob has no result view separate from its continuing AgentSession" sentence from the Agents implementation gaps; update the AgentSession implementation-gap footnote so the first-viewport most-recent result and terminal result-entry presentation are not listed as missing; keep the body spec (first viewport "and the most recent result") as the normative statement now satisfied.

### Testing

- Server: extend the `AgentSessionQuerier` spec tests (SpecTests, seeded rows) for subject excerpt bounding/absence, per-status outcome mapping, `unresolved` fallbacks, launch-envelope presence for both launch classes (agent-launch and agent-connection — both are launch-coordinator creations whose first Turn carries a JobId), launch-envelope absence when no Turn carries a JobId, follow-up-does-not-rewrite-launch, cost presence/absence, and ended-at derivation from the lifecycle anchors.
- Web: pure-function tests for the outcome derivation helpers and `turnStateFacts` outcome mapping; page/widget specs for history rows (subject, refs, start/end/elapsed/cost signals, grouping) and the header/timeline result presentation; raw-view parity check that the expanded evidence matches the same Turn observation.

## Risks / Trade-offs

- [First-input text becomes exposed on list routes where it previously appeared only in detail/transcript reads] -> bounded server-side excerpt (single line, fixed character cap), same project-scoped authentication as the transcript; the full text remains only in detail reads.
- [Large first inputs inflate list payloads] -> the excerpt is truncated server-side to a constant bound, so per-row growth is capped regardless of input size.
- [Vocabulary drift between the server's outcome normalization (D2) and the Web's header derivation (D6)] -> one table-driven TS helper in `entities/session` with tests mirroring the server mapping; both consume the same Turn statuses.
- [Sessions whose status snapshot predates persisted Turns/Inputs report no outcomes] -> they resolve to `unresolved`/absent subject, cost, and end anchor — honest, never inferred from Activity.
- [History rewrite changes familiar affordances ("Failed" group for unknown Activity, "Recent" slice, model chip prominence)] -> deliberate per the spec's result vocabulary; covered by updated page specs; model and timestamps remain as secondary row signals.
- [Positional DTO records are append-sensitive] -> new fields are appended as optional parameters following the `Origin`/`TargetId` precedent; contract tests (`CliFieldContractTests` pattern) updated.
- [`JobId` on the Turn observation widens the observation DTO] -> it is already recorded and already implied by the launch-observation surface; exposure is an id, not content.

## Migration Plan

1. Server first: add the read-model projections (D1–D3, D7) — additive optional DTO fields; existing consumers ignore unknown JSON fields, so the Web keeps working against the updated server unchanged.
2. Web: consume the new list fields in `entities/agent` DTO types, rewrite the history section (D4–D5), add the header result and launch-result presentation (D6–D7), and the timeline outcome entries (D8).
3. Docs: `design/session-timeline.md` and `docs/web-ui.md` updates (D9) land with the Web change.
4. Verification: `npm run verify` (build + full test gate) for the Web, `dotnet test` for the server suites.

Rollback: no state, events, or schema changes exist — revert the Web commit, then the server commit. Reads are the only touchpoint, so rollback needs no data repair.

## Open Questions

- Exact excerpt bound and normalization (proposed: collapse whitespace, cap ~200 characters, single line) — confirm during review.
- Should the workspace-filtered unified list (`ListUnifiedSessionsByWorkspaceAsync`) also populate the new fields? Proposed yes — it shares `UnifiedSessionListItemDto`, so the fields come free; confirm no consumer objects to the larger payload.
- Group ordering across the four history groups (proposed: Failed and Unresolved before Completed/Cancelled, mirroring "needs attention first" on the Board) and exact group labels.
- Whether the `unresolved` history group should visually distinguish "in progress" rows — proposed: no extra grouping; the per-row Activity badge is the live signal.
