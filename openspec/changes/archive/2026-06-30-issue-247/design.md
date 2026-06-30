## Context

The server models and transmits a complete session-usage read model — input/output/total/**cached**/**thought** tokens, cost (amount + currency), context-window used/size, **context-usage %**, and **health status** (green/yellow/red) — but the Web UI discards most of it. See `proposal.md` for motivation and `specs/` for requirements; this document covers *how*.

Current state, verified against the codebase:

- **DTOs already carry all 11 fields.** `AgentUsageDto` (`packages/server/.../AgentSessionReadModels.cs:6`) emits `contextUsagePercent` + `healthStatus`, both derived at read time in `AgentSessionQuerier.ToUsageDto` (`AgentSessionQuerier.cs:1125`) from `contextWindowUsed`/`contextWindowSize`. The metadata endpoint (`IssueRoutes.Sessions.cs:24`) and the workflow-run sessions endpoint (`WorkflowSessionRoutes.cs:10`) both return them.
- **`buildSessionMetadata` drops the two derived fields.** `SessionPage.tsx:65-77` maps every usage field *except* `contextUsagePercent` and `healthStatus`, so `detail.metadata.usage.contextUsagePercent` is always `null` downstream (`SessionPage.tsx:731`).
- **Client-side recompute exists and is redundant.** `widgets/session-health/model/context-health.ts` re-derives percent (prefers explicit, else `used/size`) and re-classifies green/yellow/red at 60/80 thresholds — the same thresholds as the server `ContextHealthClassifier` (`ContextHealthClassifier.cs:17`). Consumed by `ContextHealthIndicator`, `ContextHealthBar`, the `SessionPage` observability bar (`SessionPage.tsx:428-431`), and `WorkflowSessionsPanel.contextText` (`WorkflowSessionsPanel.tsx:69-78`).
- **`cachedReadTokens` / `thoughtTokens` are carried in the type** (`entities/coder-session/model/types.ts:30-31`) but rendered by zero components.
- **Realtime split.** The server emits raw token/cost/window deltas on `usage.updated` (runner payload, passed through unchanged in `AgentSessionGrain.cs:316-352`) and the *derived* `contextUsagePercent`/`healthStatus` on a **separate** `context_health_update` event (`AgentSessionGrain.cs:471-489`, only on threshold crossing or ≥10pp swing). `useSessionTimeline` already handles `context_health_update` (`useSessionTimeline.ts:691`); the two *list* hooks do not.
- **SSE handler gap.** `useWorkflowRunSessions` `usage.updated` (`useWorkflowRunSessions.ts:87-113`) omits `contextUsagePercent`/`healthStatus` that the `useCoderSessions` parity reference applies (`useCoderSessions.ts:150-151`).
- **`SessionDetail` and `StickySessionTitle` do not exist in source.** A repo-wide search finds them only under `openspec/changes/issue-247/`. The issue body's `SessionDetail.tsx:7-12` reference is stale. The only sticky surface today is the **recovery bar** (`SessionPage.tsx:972-980`), and the title header is *deliberately* non-sticky — asserted by `SessionPage.sticky.test.tsx:328-340`.
- **No aggregation endpoint** exists at issue or workflow-run scope; only project-wide timeseries/rollup (`AgentSessionQuerier.cs:762-843`). `WorkflowSessionsPanel` already aggregates `totalTokens` + cost client-side from the workflow-run sessions list it fetches (`WorkflowSessionsPanel.tsx:261-269`).

Constraints: no version-compatibility concerns (active development). Testing via vitest (`npm run test:run -w packages/web`); C# correctness enforced by `TreatWarningsAsErrors`.

Stakeholders: Web (primary), Server (no domain change expected per proposal; at most realtime enrichment — see Decision 2).

## Goals / Non-Goals

**Goals:**
- Make every server-transmitted usage field observable on the session page in one place, including `cachedReadTokens` and `thoughtTokens`.
- Establish the server-provided `healthStatus` / `contextUsagePercent` as the single source of truth; remove client-side recompute.
- Carry a usage摘要 (total tokens + context %) in the sticky region so it stays visible while the transcript scrolls.
- Surface an issue-level total (tokens + cost) consistent with the session rows.
- Deliver live context health to the workflow-sessions panel via realtime events (not full refetch).

**Non-Goals:**
- Project-level cross-issue usage rollups (already exists).
- Timeseries / trend charts (separate feature; `contextUsageHistory` is out of scope).
- Cost budgets / alerts.
- Persisting `contextUsagePercent` / `healthStatus` server-side (they stay read-time derived).

## Decisions

### Decision 1 — Server values are the source of truth; client recompute is removed

Stop dropping `contextUsagePercent` and `healthStatus` in `buildSessionMetadata` (`SessionPage.tsx:65-77`); thread them (and `healthStatus`) through `SessionMetadata.usage` into every consuming widget. `ContextHealthIndicator`, `ContextHealthBar`, the observability bar, and `WorkflowSessionsPanel` rows consume the server values directly. The recompute helpers in `context-health.ts` (`resolveContextUsagePercent`, `classifyContextHealth`, `resolveContextUsage`) are removed or reduced to pure formatting (e.g. `clampPercent`).

**Why this is safe:** the server computes `contextUsagePercent`/`healthStatus` in `ToUsageDto` *whenever* `contextWindowUsed`/`contextWindowSize` are present. So on the read path, the derived fields are null *only* when the raw window fields are also null — i.e. removing the `used/size → percent` fallback cannot cause a regression where a previously-shown indicator disappears, except in the live (non-refetched) window (handled by Decision 2). When server values are absent, widgets degrade by hiding the indicator (per `session-health` spec, graceful omission).

**Alternatives considered:**
- *Keep the `used/size` fallback as a safety net.* Rejected: it is the exact recompute the spec forbids, and it is unreachable on the read path. Keeping it reintroduces drift risk.
- *Persist derived fields server-side.* Rejected: needless storage; read-time derivation is correct and already centralized.

### Decision 2 — Realtime health flows via `context_health_update`, not by enriching `usage.updated`

Add a `context_health_update` handler to `useWorkflowRunSessions` (matching the session by `coderSessionId`/`acpSessionId`, as the existing handlers do) that applies `healthStatus`, `contextUsagePercent`, `contextWindowUsed`, `contextWindowSize`. This is the channel the server actually uses to push derived health (`AgentSessionGrain.cs:471-489`). Also apply the literal `usage.updated` spread parity (add `contextUsagePercent`/`healthStatus`, `useWorkflowRunSessions.ts:96-107`) to match `useCoderSessions` — it is harmless and satisfies the spec's field-parity scenario, but the real-time mechanism is `context_health_update`.

**Why:** the `usage.updated` transcript payload comes from the runner and does not carry the derived fields; only `context_health_update` does. A pure `usage.updated` parity fix would be field-complete on paper but data-empty in practice.

**Alternatives considered:**
- *Enrich the server `usage.updated` payload with derived fields.* Rejected: the server intentionally splits raw-usage deltas from health transitions (the latter is rate-limited by `ShouldEmitUpdate` to avoid noise). Merging would either drop the rate-limiting or duplicate classification work on both events.
- *Refetch metadata on every `usage.updated`.* Rejected: defeats the purpose of the realtime feed and adds query load.
- *Also add `context_health_update` to `useCoderSessions`.* Recommended as a follow-up for true cross-surface parity, but out of the `session-list` spec's strict scope (the session page already receives it via `useSessionTimeline`). Flagged in Open Questions.

### Decision 3 — Session usage summary is a net-new region (there is no `SessionDetail` stub to replace)

The proposal frames this as "replace the `SessionDetail` dead stub," but no such component exists in source. Implement a new **session usage summary** region on `SessionPage` that surfaces all usage fields in one place (input/output/total/cached/thought tokens, cost, context-window used/size, context-usage %, health status). Locate it alongside the observability bar (right rail or directly beneath the header) so it is visible without navigating away.

Inapplicable fields degrade gracefully: omit `thoughtTokens` when null/zero for a non-reasoning model, omit `cachedReadTokens` when no cache hit, rather than rendering misleading zeros (per `agent-session-ui` spec scenarios). Reuse the shared formatters (`formatCompact`, currency formatting) already used by `WorkflowSessionsPanel`.

### Decision 4 — Sticky usage summary via a slim sticky strip; reconcile the sticky test

Add a lightweight sticky summary strip at the top of the scroll container (`SessionPage.tsx:967-991`) that carries title + status + turn count (the identity info) **plus** a usage摘要 (total tokens + context-usage %). This satisfies the `agent-session-ui` sticky-title scenarios.

**Why a new strip, not the existing recovery bar:** the recovery bar (`SessionPage.tsx:972-980`) is health/recovery-oriented and does not show title/status/turn. The spec requires the sticky region to retain identity info *and* add usage. A slim dedicated strip is the cleanest fit and keeps the recovery bar's role intact.

**Test reconciliation:** `SessionPage.sticky.test.tsx:328-340` asserts no element outside the scroll container is sticky — its intent is "don't pin a fat duplicate header." The new strip lives *inside* the scroll container (like the recovery bar), so it does not violate that intent, but the test's selector (`[class*="sticky"]` outside scrollContainer) must be re-scoped to assert the *new* strip is the pinned surface and the outer header remains non-sticky.

**Alternatives considered:**
- *Enrich the recovery bar with usage摘要 only.* Rejected: does not satisfy "retains title/status/turn count."
- *Make the existing `SessionHeader` sticky.* Rejected: it is a rich header; pinning it wastes vertical space and directly contradicts the existing test/UX decision.

### Decision 5 — Issue-level aggregation is client-side; no new server endpoint

Compute the issue-level total (total tokens + total cost) by summing the workflow-run sessions list that `WorkflowSessionsPanel` already fetches via `useWorkflowRunSessions`. Surface it in the panel header alongside the existing `${sessions.length} sessions · … processed · … ctx · … cost` summary (`WorkflowSessionsPanel.tsx:264-269`). This makes the aggregate *consistent with the sum of its parts by construction* (it is literally the sum of the rendered rows), satisfying the `session-list` consistency scenario.

**Why no server endpoint:** the panel already holds the authoritative per-session usage; a server endpoint would return the same data and add a grain/querier method + route for no correctness gain. This keeps the change Web-only, matching the proposal's "Server: No domain change expected."

**Aggregation rules:** sum `totalTokens` and `costAmount` (grouped by `costCurrency`, reusing `summarizeCost` `WorkflowSessionsPanel.tsx:108-118`). Do **not** sum `cachedReadTokens`/`thoughtTokens`/context-window fields into the issue total — they are semantically non-additive (cache-saved overlaps input; context-window is per-session, not cumulative — the existing `usage-snapshot.ts` already treats them as non-additive). Peak context (`summarizePeakContext`) remains the correct context rollup and is already shown.

**Alternatives considered:**
- *New `GET /workflow-runs/{id}/usage` aggregation endpoint.* Rejected for now (see above); flagged in Open Questions as the escalation path if the session list becomes large enough that fetching all rows just to sum them is wasteful.

### Decision 6 — `cachedReadTokens` / `thoughtTokens` in the observability bar with omission rules

Extend the `SessionHeader` observability bar (`SessionPage.tsx:523-563`) to render `cachedReadTokens` and `thoughtTokens` alongside input/output/total. Apply the same inapplicable-field omission as Decision 3 (hide when null/zero-for-non-reasoning) so the bar stays signal-dense and does not misrepresent a non-accrued metric as an active reading (per `agent-session-ui` spec).

## Risks / Trade-offs

- `[Live health briefly absent after `usage.updated` before `context_health_update` fires]` → The server only emits `context_health_update` on threshold crossing or ≥10pp swing (`ShouldEmitUpdate`). Between deltas the panel shows the last known server value (stale-but-correct), never a fabricated one. Acceptable per spec ("stale" is disallowed only as *fabricated*; last-known server value is not fabricated). Mitigation: the next refetch (query invalidation on `coder_session_completed`) resyncs.
- [`context_health_update` event scope assumes it is delivered to workflow-run-scoped subscriptions] → `onAgentEvent` is a global subscription and handlers filter by `coderSessionId`; the event carries `SessionRuntimeBase` identifiers, so it reaches the hook. Verify in implementation with a vitest using a fake event bus (per the project's no-real-systems testing rule).
- [Removing `resolveContextUsage*` helpers is a wide blast radius] → `ContextHealthIndicator`, `ContextHealthBar`, `SessionPage`, `WorkflowSessionsPanel` all import them. Mitigation: do the removal last, after server values are threaded through; lean on existing widget tests.
- [Sticky test churn] → `SessionPage.sticky.test.tsx` must be updated, not deleted. Risk: weakening the original "no fat pinned header" assertion. Mitigation: re-scope the assertion to the new slim strip and keep asserting the outer header is non-sticky.
- [Non-additive fields excluded from issue total] → A user expecting "total cached tokens across the issue" won't see it. Trade-off accepted: summing cache-saved tokens alongside input double-counts; per-session visibility (Decision 3/6) covers the detail need.
- [`useCoderSessions` left without `context_health_update` handler] → Any surface fed only by `useCoderSessions` (not the session page) won't get live health until refetch. Mitigation: documented as a follow-up (Open Questions); the session page path is covered via `useSessionTimeline`.

## Migration Plan

This is a Web-only, behind-the-flag-free change (active development, no compat constraints). Rollout is incremental and each step is independently shippable:

1. **Data plumbing (no UI change):** fix `buildSessionMetadata` to carry `contextUsagePercent`/`healthStatus`; add the `usage.updated` spread parity + `context_health_update` handler to `useWorkflowRunSessions`. Existing widgets still recompute, so behavior is unchanged but values are now available. → run `npm run typecheck -w packages/web` + `npm run test:run -w packages/web`.
2. **Source-of-truth switch:** rewire `ContextHealthIndicator`/`ContextHealthBar`/observability bar/`WorkflowSessionsPanel` to server values; remove `context-health.ts` recompute. → re-run web tests.
3. **New surfaces:** add cached/thought tokens to the observability bar; add the session usage summary region; add the sticky summary strip and update `SessionPage.sticky.test.tsx`.
4. **Aggregation:** surface issue-level totals in `WorkflowSessionsPanel` header.

**Rollback:** revert is per-step via git revert; no schema/migration to undo. Server is untouched, so no server rollback is required.

## Open Questions

- **`useCoderSessions` parity:** should the `context_health_update` handler (and full `usage.updated` parity) be added to `useCoderSessions` in this change, or deferred as a follow-up? The `session-list` spec scopes the fix to `useWorkflowRunSessions`; `session-health` speaks of cross-surface single-source-of-truth. Lean: defer `useCoderSessions` realtime to a follow-up unless a non-session-page consumer is identified during implementation.
- **Aggregation endpoint trigger:** at what session-count-per-issue does client-side summation become wasteful enough to justify `GET /workflow-runs/{id}/usage`? Defer until observed; the current per-issue session count is small.
- **Summary region placement:** right rail vs. beneath-header for the session usage summary. Confirm against the existing `xl:flex-row` layout (`SessionPage.tsx:956`) during implementation; the sibling-sessions sidebar already occupies the right rail on `xl`, so beneath-header may avoid crowding.
