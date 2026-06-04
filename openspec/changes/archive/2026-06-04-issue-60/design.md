## Context

Session records are Mohist's primary audit trail for agent execution, but the current model only stores the requested model and coarse lifecycle data. ACP already reports the actual model in session responses, token usage and cost through prompt responses and usage notifications, model switches through `config_option_update`, and runtime liveness failure categories in the runner. The missing work is to preserve those signals through the runner event stream, server grain/domain/storage, API read models, and Web UI.

The change must remain additive because existing databases, existing session rows, and existing frontend consumers depend on the current session contract. The existing `Model` field keeps its meaning as the intent/requested model; the new observability fields describe what the runtime actually did. Stakeholders are users inspecting session detail/activity feeds, operators analyzing model/cost/failure behavior, and implementers maintaining the runner/server/UI event pipeline.

## Goals / Non-Goals

**Goals:**

- Persist and expose the resolved model separately from the requested model.
- Capture ACP usage and cost signals as auditable session events and accumulated session-level counters.
- Surface context window usage, failure category, and tool statistics in session APIs and UI components.
- Populate existing activity-card task progress from workflow state instead of returning a placeholder.
- Keep the migration and API contract backward compatible through nullable/additive fields.

**Non-Goals:**

- Implement billing reconciliation, provider-specific pricing, or cost normalization across currencies.
- Backfill historical usage, model, failure, or tool statistics for existing sessions.
- Replace the existing free-text `FailureReason`; it remains available for diagnostics.
- Add interactive controls to `SessionPage`; the page remains read-only.
- Introduce a new analytics subsystem or aggregate cross-session reporting.

## Decisions

### Decision 1: Store intent model and resolved model as independent fields

`WorkflowAgentSession.Model` remains the requested/intent model passed by the runner. A new nullable `ResolvedModel` field stores ACP's actual model from `newSession` / `resumeSession` `models.currentModelId`, and later `config_option_update` notifications update only `ResolvedModel`.

Rationale: users need to know both what Mohist asked for and what the ACP runtime actually used. Keeping both fields avoids silently overwriting intent data and makes model substitution or runtime fallback visible.

Alternatives considered: overwrite `Model` with the ACP value; rejected because it loses the original request. Fall back `ResolvedModel` to `Model` when ACP does not report a model; rejected because it would blur unknown resolved state with confirmed runtime state.

### Decision 2: Introduce an additive `agent_usage_update` session event

The runner emits `agent_usage_update` for both `PromptResponse.usage` and ACP `usage_update` notifications. The payload carries optional token deltas, optional cost, optional context window snapshot, and normal session routing fields. The event is registered in backend and frontend SSE event registries so live clients can refresh session summaries.

Rationale: usage is part of the session audit trail, not just a derived row update. Persisting the raw event keeps per-turn history available while the session row provides fast summary reads.

Alternatives considered: update the session row directly without appending an event; rejected because it would remove auditability. Reuse existing liveness/status event types; rejected because usage has a distinct payload and invalidation behavior.

### Decision 3: Accumulate usage in the session domain model

`WorkflowAgentSession` owns transitions such as `ApplyUsage`, `UpdateResolvedModel`, and `RecordToolCall`. `WorkflowAgentSessionGrain.AppendEventsAsync` parses incoming events, calls these domain transitions, and persists row changes with appended event rows. Token and cost fields accumulate deltas; context window fields store the latest reported snapshot.

Rationale: centralizing mutation rules in the domain model keeps storage, grain, and query code shallow. It also lets the domain enforce non-decreasing token counters and terminal-session handling consistently.

Alternatives considered: compute totals at query time from event rows; rejected because activity/session list reads would become expensive and harder to paginate. Store only cumulative ACP totals; rejected because ACP signals are specified as optional deltas/snapshots and may be partial.

### Decision 4: Keep cost simple and currency-preserving

`CostAmount` accumulates reported cost amounts. `CostCurrency` stores the most recent reported currency. The system does not convert, validate exchange rates, or split mixed-currency totals.

Rationale: ACP is the source of truth for reported cost, and the immediate requirement is observability, not accounting. Preserving the latest currency code is simple and matches the spec while avoiding misleading conversion logic.

Alternatives considered: reject mixed currency updates; rejected because it could drop valid runtime telemetry. Store a per-currency map; rejected as unnecessary schema complexity for this pass.

### Decision 5: Count tool summaries from appended tool events

`AppendEventsAsync` reuses the existing tool-call parsing path. A parsed `tool_call` increments `ToolCallCount`; a parsed terminal failed/error `tool_call_update` increments `ToolErrorCount`, capped so errors do not exceed calls.

Rationale: the grain already processes the canonical session event stream, so it is the right place to maintain persisted counters. Reusing the parser avoids a second interpretation of tool payloads.

Alternatives considered: count tool cards in the query projection; rejected because it would require scanning event history for every summary read. Count every `tool_call_update`; rejected because updates are lifecycle changes, not new calls.

### Decision 6: Expose new fields through existing DTOs and components

The storage row, domain model, read models, API DTOs, frontend types, `SessionHeader`, `SessionCard`, and `SessionPage` all receive the same observability fields with stable camelCase JSON names. UI renders compact badges and omits absent values instead of showing empty placeholders.

Rationale: existing session surfaces should become more informative without introducing new endpoints or new UI routes. Nullable additive fields preserve compatibility for old rows and clients.

Alternatives considered: create a separate usage endpoint; rejected because session list/detail/activity views need these values inline. Always render zero values for missing data; rejected because missing telemetry is different from confirmed zero usage.

### Decision 7: Reuse workflow projection services for task progress

`WorkflowAgentSessionQueryService.ToActivityCard` obtains current-stage task progress via existing workflow query/projection accessors and passes the resulting `{ completed, total }` into `ActivityCardDto.TaskProgress`. It returns `null` only when the workflow run or current stage cannot be resolved.

Rationale: the data already exists in workflow state, and reusing the projection path keeps activity cards consistent with active-agent projections.

Alternatives considered: recompute task progress directly from lower-level workflow state in the session query; rejected because it duplicates business rules. Persist task progress on the session row; rejected because it is workflow state, not intrinsic session metadata.

## Risks / Trade-offs

- [ACP payload shape drift] -> Parse usage/model fields defensively and ignore missing fields instead of synthesizing values.
- [Duplicate or replayed events inflate counters] -> Keep event application idempotency aligned with existing append semantics; count only parsed `tool_call` starts and failed terminal updates.
- [Usage updates after terminal sessions] -> Persist the event for audit but do not mutate terminal session counters or status.
- [Mixed cost currencies produce ambiguous totals] -> Preserve the latest currency code and document that cross-currency normalization is out of scope.
- [Nullable fields complicate UI rendering] -> Frontend types model fields as nullable and components omit unavailable badges.
- [Context window size is required by UI but omitted from the initial storage list] -> Include `ContextWindowSize` alongside `ContextWindowUsed` in domain/row/DTO if the UI needs `used / size`; otherwise render used-only fallback.
- [Task progress lookup can add query cost] -> Reuse existing workflow projection accessors and return `null` when the workflow run cannot be loaded instead of blocking activity rendering.

## Migration Plan

1. Add nullable observability columns to `WorkflowAgentSessions` through an additive EF Core migration: `ResolvedModel`, token counters, cost fields, context window fields, `FailureCategory`, `ToolCallCount`, and `ToolErrorCount`.
2. Extend domain, row mapping, and DTO/read-model projections without removing or renaming existing fields.
3. Update runner event emission for resolved model, `agent_usage_update`, liveness classification, and terminal `failureCategory`.
4. Update `WorkflowAgentSessionGrain.AppendEventsAsync` to apply usage/model/failure/tool transitions while still persisting raw events.
5. Register `agent_usage_update` in backend/frontend SSE event type maps and frontend invalidation paths.
6. Update frontend types and UI components to render new values when present.
7. Add or update tests across runner event emission, grain/domain transitions, migration/read-model mapping, activity task progress, and UI formatting behavior.

Rollback is safe at the application level because all API fields and database columns are additive. If a deployment must roll back code after the migration, older code should ignore the extra columns. If the migration itself must be rolled back in a development database, drop only the newly added columns; production rollback should prefer leaving unused additive columns in place to avoid data loss.

## Open Questions

- Should `ContextWindowSize` be persisted as a first-class session column to support exact `used / size` display on historical reads, or should only the latest usage event carry the size?
- Should unknown `failureCategory` values be rejected, normalized to `null`, or preserved as raw strings for forward compatibility with future runner categories?
- Do ACP `PromptResponse.usage` values always represent per-turn deltas, or can some providers return cumulative totals that need de-duplication?
- Should cost amounts use `decimal` in .NET storage/domain code instead of floating-point `REAL` for future accounting precision, even though this change is observability-only?
