# Design: Agent Execution History and Session Timeline Interpretation (issue-558)

## Context

Every fact this change needs already exists and has exactly one authoritative owner:

- **AgentJob lifecycle and terminal result** — the `AgentJobGrain` state, mirrored to the relational `AgentJobs` table (`AgentJobRow` carries `Status`, `SubmittedAt`, `TerminalAt`, `AgentSessionId`, `InitialInputId`, `InitialTurnId`, `IssueNumber`, `Title`, and the state JSON with `TerminalResult`).
- **Session facts** — the `AgentSession` grain state mirrored to `AgentSessions` rows: `Inputs` (text, acceptance, `JobId`), `Turns` (`AgentTurnRecord` with `Status`, `Result`, `InputIds`, `JobId`, `RecordedAt`/`UpdatedAt`), `UsageSummary`, `Activity`, and the launch context-ref labels (`AgentSessionContextRefs`).
- **Resolved model** — per-session transcript reduction (`TranscriptReductions.LoadEventSummariesAsync`).
- **Context refs** — launch-stamped labels, already projected into the `UnifiedSessionContextRefsDto` envelope shape used by list/summary reads.

The gap is presentation. The Web Agent detail page (`packages/web/src/pages/agent-detail/ui/AgentDetailPage.tsx`) renders sessions as indistinguishable rows, repeats the same session across Running/Failed/Ended/Recent sections, and — verifiably, in code — groups `activity === 'unknown'` under **Failed**, contradicting the certainty vocabulary (`docs/agent-sessions.md`, "Why Unknown Fails Closed"). The CLI (`mo agent job list` / `mo agent job view`) shows only lifecycle timestamps. The Session timeline (#427, `design/session-timeline.md`) explains the process but its Web implementation is partial, and history records, the Session page, and any exported result do not yet share one execution vocabulary.

Constraints that shape this design:

- History is a **read-only projection**: it must never re-arbitrate Job, Turn, or Session state (`design/conventions.md` — facts/claims/settlement; `unknown` is a first-class value).
- Job result and Session Activity are different facts with different owners; they must stay distinct on every surface.
- Usage is recorded at **Session level only**; there is no per-Turn or per-Job cost fact, so cost cannot be divided or allocated.
- The existing `GET .../agents/{ref}/sessions` and `GET .../agents/{ref}/jobs` endpoints keep their current contracts for current consumers.
- #589 (settlement) introduces `blocked`/`unknown` outcome states for Workflow task arbitration; this change coordinates in vocabulary only and must not reinterpret those states.

Reference: motivation in [`proposal.md`](proposal.md); requirements in [`specs/agent-execution-history`](specs/agent-execution-history/spec.md), [`specs/session-timeline-interpretation`](specs/session-timeline-interpretation/spec.md), [`specs/session-result-export`](specs/session-result-export/spec.md).

## Goals / Non-Goals

**Goals:**

- One canonical Agent execution history record per execution (one per Job; one per Turn not represented by a Job), carrying task summary, outcome, result summary with failure reason, context refs, timing, model, and honestly attributed cost.
- A Server read API exposing that history, project-scoped and agent-scoped, ordered by recency with status filter and limit, without touching the existing jobs/sessions list endpoints.
- The Web Agent detail page presents history records — distinguishable, never duplicated across sections, `unknown` never presented as failed — linking into the Session page anchored to the Turn.
- The Session page timeline completes the #427 model (input state, domain-action recognition, collapse rules with breakers, unknown presentation, context envelope) and keeps its anchor across refresh.
- A Session result export (Web action + CLI command) that describes the same execution with the same vocabulary, understandable standalone.
- CLI reads return the full history/export contract in table and `--json` form.

**Non-Goals:**

- No change to Input/Turn lifecycle, recovery, stop, settlement, or dispatch semantics; no new transcript facts.
- No per-Turn or per-Job usage measurement or cost allocation.
- No re-arbitration or mutation of any authoritative state; no materialized history store.
- No change to the external public Session event stream (`design/agent-api.md`); the export is a project-authenticated read, not a public projection.
- No rebuild of the Session page; the timeline alignment is vocabulary, anchoring, and the remaining #427 derivation gaps.

## Decisions

### D1. Server-owned read projection, computed at read time — no new store

A new `AgentExecutionHistoryQuerier` in `packages/server/src/Mohist.Server/Sessions/Services` composes two existing reads per request: the `AgentJobs` mirror (via `AgentJobQuerier`/`AgentJobStore`) and the session state rows (via `AgentSessionQuery.ListByLabelsAsync` with the `project-id` + `agent-id` + `source-kind` labels — the exact index path `ListAgentSessionsAsync` already uses). The join key is `AgentJobRow.AgentSessionId` + `InitialTurnId` against `AgentTurnRecord.JobId`.

- **Alternative: materialized history table written through on every lifecycle transition.** Rejected: it creates a second durable copy of facts that already have one witness each, needs reconciliation after every crash, and the list read is low-frequency over bounded rows (limit clamp `[1, 200]`, default `50` — the established list convention).
- **Alternative: assemble the history client-side in Web and CLI from existing endpoints.** Rejected: the spec requires one contract across Web, CLI, and export; duplicating join/dedup logic in two clients guarantees drift, and the CLI would need several round trips per read.

### D2. Record identity and the dedup rule

- A **Job record**'s identity is the Job id. It is emitted for every visible `AgentJobs` row of the Agent (same `LaunchVisibility == visible` gate as `AgentJobQuerier.ListByAgentAsync`).
- A **Turn record**'s identity is `(SessionId, TurnId)`. A Turn is suppressed **iff** its `AgentTurnRecord.JobId` resolves to a Job record already present in the same response — this is the launch case, where the Job record *is* the execution record. Follow-up Turns (no `JobId`) always produce their own record.
- Suppression is decided per response, not per stored row: if a Job row is filtered out by the status filter, its launch Turn still appears as a Turn record (with its own authoritative Turn status), so filtering never deletes an execution from the view. No surface may render one record in two sections simultaneously; the Web page renders one chronological list (D7).

**Alternative considered: key the launch record on `(SessionId, InitialTurnId)` instead of Job id.** Rejected: the Job is the work-result owner (issue-479 vocabulary); using its identity keeps Job result facts (terminal message, failure reason, exit code) on their owning record instead of borrowing Turn facts.

### D3. One record contract with explicit attribution

```text
AgentExecutionHistoryItemDto
  kind                 # "job" | "turn"
  jobId                # job records only
  sessionId, turnId    # turn records carry turnId; job records carry the launch turnId for anchoring
  taskSummary          # bounded, single line (D6); absent if no input text exists
  status               # normalized vocabulary (D4), authoritative owner status
  result               # { message?, output?, failureReason?, failureCategory?, exitCode? } — terminal facts only
  contextRefs          # the same envelope shape as UnifiedSessionContextRefsDto; null when the Session had none
  startedAt, endedAt?  # authoritative timestamps; endedAt absent while nonterminal
  durationSeconds?     # computed only when both timestamps exist
  model?               # session-resolved model; session attribution
  cost?                # { amount?, currency?, attribution: "session" } — never per-turn
```

The JSON policy is the existing absent-when-empty idiom (`DefaultIgnoreCondition = WhenWritingNull`): facts that do not exist are omitted, never nulled, never zeroed, never defaulted. `attribution: "session"` is mandatory whenever cost is present, because `UsageSummary` is Session-level; the projection must not divide, prorate, or fabricate a per-Turn figure. Model is presented without claiming per-Turn measurement (same source as the summary reads).

**Alternative considered: expose raw `usage` totals on every record.** Rejected: totals invite per-record misreading; the history contract carries the attributed cost figure plus the attribution level, which is all the recorded facts support.

### D4. Status vocabulary: authoritative owner status, `unknown` first-class, Job ≠ Session Activity

- Job record `status` comes from the Job's authoritative status; Turn record `status` from the Turn's. Session `Activity` (`idle`/`active`/`unknown`) is **never** copied onto a record.
- One normalized filter/render vocabulary for the history surface: `pending | running | executing | completed | failed | stopped | unknown`. The legacy runner-protocol `cancelled` alias normalizes to `stopped` at this read boundary — the same normalization `ListAgentSessionsAsync` already performs and documents. `mo agent job view` keeps its own wire vocabulary; only the history surface normalizes.
- The status filter accepts only values from that vocabulary; anything else is a 400 (mirroring `AgentJobReadRoutes`' rejection pattern).
- Presentation rule (all surfaces): `unknown` renders with unknown styling and never under a Failed group, with a failure icon, or with failure wording. A failed Job whose Session remains open renders the Job failure only — the Session itself is not "failed".

### D5. Read API: one history endpoint, existing lists untouched

`GET /api/projects/{projectRef}/agents/{agentRef}/history?status=&limit=` in a new `Api/AgentExecutionHistoryRoutes.cs`, registered beside `AgentSessionListRoutes` / `AgentJobReadRoutes`:

- Project isolation via the `ProjectResolutionEndpointFilter` + agent resolution via `AgentRefResolver` (404 on unknown Agent), matching the sibling routes.
- Ordering: most recent activity first — Job records by `max(TerminalAt, SubmittedAt)`, Turn records by `UpdatedAt ?? RecordedAt`, merged and sorted descending, capped by `limit` clamped to `[1, 200]` (default `50`).
- The existing jobs and sessions list endpoints are unchanged.

**Alternative considered: extend the existing `GET .../agents/{ref}/jobs` response with the history fields.** Rejected: its contract is consumed today (CLI table shape, availability polling); widening it breaks "bare lifecycle" consumers and mixes two granularities in one list.

### D6. Task summary derivation

The task summary is derived from the SessionInputs that opened the execution, read from the session state (the same source as `AgentSessionObservationMapper.Inputs`):

- Turn record: the text of the Turn's first accepted input (`turn.InputIds` → input `Text`).
- Job record: the input with `Id == InitialInputId`; fallback to the launch Turn's transcript `PromptText` (`AgentSessionTranscriptTurnRow`); fallback to `AgentJobRow.Title`; absent if none exists.
- Bounded at read time (single line, ~200 chars, truncation marker) — list responses must not echo unbounded input text. If the Session row no longer resolves (purged), the Job record still renders with its own Job facts and the task/model/cost fields absent.

### D7. Web Agent detail: one history list, anchored links

`packages/web/src/pages/agent-detail` replaces `useAgentSessions` + the four `SessionSection` groups with a new history query (`entities/agent/api`) hitting D5:

- One chronological list of records; each row shows task summary, status, result summary, context chips, timing, model, and attributed cost (cost labeled "session-level"). Records appear exactly once; filtering is by status chips over the same list, never by duplicating rows into sections.
- `unknown` gets its own presentation (grey/unknown icon and label) — the current `failedSessions = filter(activity === 'unknown')` grouping is deleted.
- Row activation navigates to `/sessions/{sessionId}?turn={turnId}` (Job records anchor to `InitialTurnId`).

**Alternative considered: keep the Running/Failed/Ended/Recent groups but fix membership.** Rejected: the spec forbids the same record in two sections; four fixed groups over a mixed Job/Turn list cannot guarantee that without duplicating or hiding records.

### D8. Session page: shared vocabulary, stable anchor, timeline completion

- **Shared vocabulary module.** One Web module (e.g. `entities/agent/model/execution.ts` or `shared/lib`) defines the status labels/icons, failure-category interpretation, and context-ref envelope rendering used by (a) history rows, (b) the Session page header/timeline, and (c) nothing else — the export is server-side. The Session page presents the same context envelope with the same absence semantics as the history record. Turn outcome on the Session page renders from the same `AgentTurnObservation` facts the server projection reads, so a discrepancy between the history record and the timeline for the same Turn identity is impossible by construction (both are pure functions of the same facts).
- **Anchoring.** The anchor is the `turn` URL search parameter on `/sessions/:sessionId` — not component state — so a refresh (and a shared link) restores the same position and interpretation. The page scrolls to the Turn's first timeline item on load and after data refetch. The raw/interpreted view toggle already preserves scroll by item identity (`SessionDetailShell`); anchoring composes with it.
- **Timeline completion (remaining #427 gaps, per `design/session-timeline.md` status section):** move SessionInput acceptance/delivery state into the timeline as `input` items; add `mo`-command domain-action recognition (client-side parse of bash-tool commands against the CLI verb table, linking Issue/Run targets); implement the ≥3 consecutive same-class low-salience collapse where `error` / `domain-action` / `input` / `message` / `status` / `boundary` items break runs and never collapse; render Compact/Reset boundaries; keep `unknown` distinct from idle and failed. Classification stays a pure client derivation from Server facts — no state inference from heartbeats or item order, no new Server fields.
- `widgets/session-transcript` changes are limited to this derivation/vocabulary alignment; the raw diagnostic view stays the same facts in fact order.

### D9. Export: one server-side read, two surfaces

`GET /api/projects/{projectRef}/sessions/{sessionId}/export?turn={turnId}` returns a standalone `SessionResultExportDto`:

```text
sessionId, turnId                     # stable identity of the exported execution
jobId?                                # only when the exported Turn is Job-bound; never inherited
taskSummary?, contextRefs?            # same derivation and absence semantics as history
status, result?                       # same vocabulary; unknown exports as unknown
startedAt, endedAt?, durationSeconds?
model?, cost?                         # session attribution, labeled
```

- Reuses the D1 projection against one session: default Turn is the Session's most recent Turn; `?turn=` selects any other. No generation timestamp is embedded — repeating the export must produce the same facts.
- The Web Session page offers an export action (fetch + browser download as JSON); the CLI offers `mo session export` (D10). Both produce the byte-identical contract from the same endpoint, so "one execution reads the same everywhere" holds mechanically.
- Read-only by construction: it is a GET over existing rows; it cannot mutate Session/Turn/Job state or trigger recovery/settlement. Follow-up Turn exports carry no `jobId`; launches carry the Job identity; unknown outcomes export as `unknown`.

**Alternative considered: build the export client-side from the summary DTO.** Rejected: the CLI would reimplement the derivation, and the standalone-understandable guarantee (identities + context + result in one document) would live in two places.

### D10. CLI surfaces

- New `mo agent history <agentRef> [--status ...] [--limit ...]` (in `MohistCliCommands.Agent.cs`): table columns for task, status, result summary, context, timing, model, and attributed cost; `--json` carries the D3 contract with absent facts omitted; each row prints the Session id (and Turn id for Turn records) so `mo session view <id>` navigates directly. Registered in `ResourceOutputCatalog` with a new `TableShape`.
- `mo agent job view <job-id>` is enriched with the same interpretation fields (task summary, context, model, attributed session cost) resolved through the projection by Job id; `mo agent job list` keeps its current shape for existing consumers.
- New `mo session export <session-id> [--turn <turn-id>]` printing the export contract (human summary by default, `--json` for the raw contract).
- `unknown` renders as `unknown` in every table — never "failed".

## Risks / Trade-offs

- [Read-time join deserializes N session state JSONs per history request] -> Bounded by the `[1,200]` limit clamp and the existing label-indexed query path; the list read is low-frequency UI/CLI traffic. If it ever heats up, the projection can add a per-session cache without changing the contract.
- [Cost shown on a Turn record could be misread as that Turn's cost] -> The `attribution: "session"` field is mandatory whenever cost is present, and every surface renders it as "session-level"; specs pin that no division/allocation may occur.
- [Broken Job↔Turn linkage (older rows, missing `AgentSessionId`/`InitialTurnId`) could duplicate an execution] -> Suppression relies only on `AgentTurnRecord.JobId`, which the Session stamps at input acceptance; if linkage is genuinely absent, the Job record and Turn record each render their own authoritative facts — honest duplication of unknown linkage, not fabricated identity. A test pins the linked case.
- [Status-filter interplay with dedup could hide an execution (Job filtered out, launch Turn suppressed)] -> D2: suppression is evaluated per response against the *returned* Job set, so the launch Turn re-appears as a Turn record when its Job is filtered out.
- [`cancelled` → `stopped` normalization differs from `mo agent job view`'s raw vocabulary] -> Documented at the read boundary (the session list already normalizes this alias); history is a new surface with one vocabulary, and the underlying fact is unchanged.
- [Task summary echoes user input text into list responses] -> Same principal-scoped authentication as the session summary (which already exposes inputs); summaries are bounded to one truncated line (D6).
- [Client-side domain-action recognition could misparse a `mo` command] -> Recognition degrades to the honest `shell` fallback (`Ran X`) — never speculative promotion; the rule is already specified in `design/session-timeline.md`.
- [Timeline derivation drift between interpreted and raw views] -> Both views consume the same fact feed in the same order; the raw view may not reorder/filter — pinned by widget tests.
- [Export mistaken for an external public API] -> It is a project-scoped authenticated read with the summary's field policy (no raw payloads, no runtime identity); the public event stream in `design/agent-api.md` remains the only external contract.

## Migration Plan

Purely additive read paths; no schema change, no backfill, no lifecycle change. Deploy Server first, then Web and CLI (which only add queries/commands against the new routes).

1. **Server projection + routes:** `AgentExecutionHistoryQuerier`, `AgentExecutionHistoryItemDto`, `GET .../agents/{ref}/history`, `GET .../sessions/{id}/export`; unit tests for dedup (launch / follow-up / filtered), unknown-not-failed, absent-fields, session-attributed cost, status-filter rejection, project isolation.
2. **Web entities + Agent detail page:** history types/query in `entities/agent/api`; replace the session-row sections with the history list; unknown presentation; anchored record links. Update `docs/web-ui.md` (Agent detail history section + implementation gaps).
3. **Session page:** shared vocabulary module; `?turn=` anchoring with refresh retention; context envelope in the header; timeline completion items from D8 (input state, domain-action recognition, collapse-with-breakers, boundaries); export action.
4. **CLI:** `mo agent history`, `mo session export`, enriched `mo agent job view`; table shapes + tests; update `docs/cli-reference.md`.
5. **Docs:** `docs/agent-sessions.md` (history projection section), `design/session-timeline.md` status update (implementation gaps closed by this change), `docs/web-ui.md`.

Verification: `npm run test:fast` per package, then the full `npm run verify` gate.

**Rollback:** revert the release — no state was written, no migration exists, and the pre-existing jobs/sessions endpoints never changed, so older Web/CLI builds keep working against either version.

## Open Questions

- Should Turn records include `agent-connection`-sourced Sessions (Slack conversations), or only `agent-launch`? The D1 query already supports both source kinds (the unified session list includes both); leaning include, since a connection Turn is an execution of the same Agent — confirm during implementation.
- Result-summary bounding: the history table shows one line, but the record contract carries full `message`/`output`. Confirm whether `output` should be truncated server-side in the list (it can be large) or bounded only at render — leaning server-side bounded summary field plus a link to the Session/Job detail.
- Whether the Web history list needs a client-side "collapse older" pagination beyond the `limit` cap, or infinite scroll on the existing endpoint is enough.
