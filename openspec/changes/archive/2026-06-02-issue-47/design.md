## Context

The session transcript page at `/issues/{number}/workflow/sessions/{sessionName}` is meant to be a faithful read-only projection of the real opencode / coder agent conversation. In `WorkflowAgentSessionQueryService.BuildTranscriptAsync` the projection is hard-coded to a single fixed turn per session:

- `turn.user.text` is taken from `session.Title` (e.g. `Cover backend projection and progress behavior`) even though the runner records the full real prompt in a `mohist_prompt` workflow session event. On `/issues/8/workflow/sessions/T-005.1` this hides a ~7.5k-character gap between what the agent actually saw and what the page shows.
- All assistant parts are accumulated in one `turn.assistant` list, with reasoning, text, and tool chunks collapsed into a single accumulator per type and closed only at session boundary. `tool_call` / `tool_call_update` are merged in place, but reasoning / text streams keep running across tool boundaries.
- `agent_liveness_status` and `agent_session_terminal` events are filtered out of the latest-events query and never projected as transcript parts, so a refreshed replay is missing liveness transitions and the closing terminal state that the live SSE-driven page already shows.
- Historical sessions that have no `mohist_prompt` event have no explicit missing-prompt state; the page just shows whatever `session.Title` is as if it were the user prompt.

Earlier issues (#158, #184, #190, #205, #220) already moved the rest of the transcript onto a real-conversation shape. This change closes the remaining gap on the same source-of-truth principle: the canonical `workflow_agent_session_events` stream is the single authority, and `mohist_prompt` events open real transcript turns.

The runner already emits everything we need (`mohist_prompt`, `agent_message_chunk`, `agent_output_chunk`, `agent_thought_chunk`, `tool_call`, `tool_call_update`, `agent_liveness_status`, `agent_session_terminal`, plus the `coder_recovery_status` SSE event projected by the web). The proposal and both `spec.md` files (see `openspec/changes/issue-47/`) pin down the desired product shape; this design explains how to implement it without expanding the workflow model, the runner, or the API surface.

## Goals / Non-Goals

**Goals:**
- Make `mohist_prompt` events the canonical source of truth for the user prompt of each transcript turn. Populate `turn.user.text` (and `kind`, `sentAt`, `title`, `outputPath`, `contextFiles`) from the event payload; never fall back to `session.Title` as prompt text.
- Split the assistant part list into one turn per `mohist_prompt` in event order; resume / follow-up / retry / recovery prompts each open a new turn.
- Preserve natural reasoning / text / tool interleaving: close the active opposite stream part when a new chunk type arrives, instead of collapsing all reasoning or all text into one giant part per turn.
- Project `agent_liveness_status`, `agent_session_terminal`, and `coder_recovery_*` events as divider / error parts in the same position the live page shows them.
- Render historical sessions that have no `mohist_prompt` event with an explicit `legacy-missing` prompt state; never substitute `session.Title`, `session.SessionName`, or `session.Id` as the user prompt.
- Keep the web prompt card expanding / copying the full `mohist_prompt.data.text` and keep raw tool payload access (input, output, metadata, details) available through the existing tool disclosure.

**Non-Goals:**
- Do not change the prompt sent to opencode.
- Do not introduce a separate transcript snapshot table or persist new fields; the existing `workflow_agent_session_events` stream stays the single source of truth.
- Do not redesign the prompt card, the tool disclosure, or the overall page layout; align existing components with the new payload.
- Do not solve ACP concurrent-session routing (already tracked elsewhere).

## Decisions

### D1: Rewrite `BuildTranscriptAsync` to iterate the event stream in `Sequence` order and emit one turn per `mohist_prompt`

The current implementation builds one fixed turn up front and runs `BuildAssistantParts` against every event. The replacement iterates `WorkflowAgentSessionEventRow` rows ordered by `Sequence` and folds them into a list of transcript turns:

- A new `TranscriptTurn` is appended each time an event with `Type == "mohist_prompt"` is encountered. The turn's `user` object is built from the event's `PayloadJson` (`text`, `kind`, `sentAt` from `CreatedAt`, `role = "mohist"`, plus `title`, `outputPath`, `contextFiles` when present). The turn's `startedAt` is the event's `CreatedAt`.
- All other event types (`agent_message_chunk`, `agent_output_chunk`, `agent_thought_chunk`, `tool_call`, `tool_call_update`, `agent_liveness_status`, `agent_session_terminal`, `coder_recovery_status` recorded through the same stream) are routed into the most recently opened turn's `assistant` list. Assistant parts therefore end up in the turn whose prompt preceded them in event order, in event order within that turn.
- When the session has zero `mohist_prompt` events, the projection emits exactly one turn whose `user.kind = "legacy-missing"`, `user.text` is a clearly labeled missing-prompt string, and whose `assistant` list still contains every other event projected as parts.
- Each turn's `completedAt` is the `createdAt` of the next `mohist_prompt` event, or `session.CompletedAt`, or the last event's `createdAt`, in that order.

**Alternatives considered:**
- Keep the single-turn shape and only swap `session.Title` for `mohist_prompt.data.text`: rejected because it does not produce multiple turns for resumed / follow-up rounds and still leaks `session.Title` when no prompt exists.
- Build turns from a separate turn table: rejected by the non-goal "do not persist new transcript snapshots"; the event stream is already authoritative.

### D2: Preserve reasoning / text interleaving by closing the active opposite stream part on chunk-type change

The current `BuildAssistantParts` keeps one `TextAccumulator` and one `ReasoningAccumulator` for the entire session and concatenates into them whenever a chunk arrives, so a reasoning chunk that arrives after a tool call is still appended to the reasoning stream that opened at the start of the session. The new projection closes the active stream part the moment a new chunk type arrives:

- `agent_message_chunk` and `agent_output_chunk` accumulate into the active text part for the current turn. If a tool or thought chunk arrives next, the text part is closed at the new event's `createdAt` before the tool / thought part is appended.
- `agent_thought_chunk` accumulates into the active reasoning part for the current turn. The same closure rule applies.
- The first event of a given stream type after a switch starts a new part with that event's `createdAt` as `startedAt`. A later chunk of the same type resumes the existing part (so a long message split across chunks is still one part) until the type switches again or the turn closes.
- This produces `thought → tool → thought → text` and `text → tool → text → reasoning` orders in the part list, matching what the live page already shows through `useSessionTranscript`.

**Alternatives considered:**
- Keep one accumulator per type for the whole session and rely on the frontend `applyReasoningReorder` post-pass to undo the collapse: rejected because the post-pass only moves same-second blocks and cannot recover order across tool boundaries. The backend should be the source of truth.
- Add a new accumulator per (turn, type) and never close mid-turn: rejected because that would let one reasoning block span a tool call, which is exactly the "giant reasoning wall at the top of a turn" failure mode called out in the proposal.

### D3: Tool part merging keeps first-observed position and all raw payload fields

Tool projection already lives in `BuildAssistantParts` / `ParseToolCall` / `MergeToolPart`. Two refinements:

- Per-turn tool index map (one `Dictionary<string, int>` per turn, keyed by `toolCallId`). On the first `tool_call` for a given id within a turn, append the tool part and remember its index. On later `tool_call_update` events for the same id, replace the element at the remembered index with the merged projection, so the part never moves position.
- `MergeToolPart` already preserves `rawInput`, `rawOutput`, `metadata`, `details` from either side; this change confirms those fields are written back into the merged part when the update carries them, and that `status`, `title`, `completedAt` follow the existing precedence rules (`pending` update does not overwrite, terminal status closes `completedAt`). The merge result keeps the first event's `id` and `createdAt` as the part's identity, so the frontend sees a stable id and the same `rawInput` / `rawOutput` / `metadata` / `details` reach `ToolCallCard` (or its `tool-registry.tsx` equivalent) unchanged.
- The current projection also reads `nested` payloads through `payload.toolCall.*`; that behavior is preserved and not modified by this change.

**Alternatives considered:**
- Re-emit the tool part on every update (moving it to the end of the list): rejected because the part would jump around the turn and break the "first-observed position" acceptance criterion.
- Persist a separate tool snapshot table: rejected by the non-goal on new persistence.

### D4: Project liveness, terminal, and recovery events as divider / error parts at their event position

The current code filters `agent_liveness_status` and `agent_session_terminal` out of the latest-events query (`LoadLatestEventsAsync`) and never projects them. The new projection:

- `agent_liveness_status` → a `divider` part with `label` derived from the payload (`status` plus `lastDataAt` / `lastActivityType` / `failureReason` when present). Inserted at the position of the event in the current turn's part list. Mirrors the live `agent_liveness_status` handler in `useSessionTranscript.ts` (`onAgentEvent('agent_liveness_status', …)`) which today appends an `ErrorPart` of `kind: 'recovery'`. Both are acceptable shapes; we will pick one and keep the other compatible. See Open Question 1.
- `agent_session_terminal` → a closing `error` part on the current turn: `kind: 'failed' | 'cancelled' | 'timeout' | 'completed'` mapped from `payload.status`, `message = payload.failureReason` when present. Inserted at the terminal event's `createdAt`, which also becomes the turn's `completedAt` if no later event closes it.
- `coder_recovery_status` (the SSE event used by the web live path; the same shape is forwarded to the session event stream where it is recorded): a `divider` part (or `error` part with `kind: 'recovery'`) using the live handler's mapping (`detected → 'Recovery detected'`, `recovering → 'Recovery in progress'`, `recovered → 'Recovery succeeded'`, `failed → 'Recovery failed'`).

**Alternatives considered:**
- Keep these events as raw `WorkflowLogItem`s only and not project them as transcript parts: rejected because refreshed pages must match live pages per the spec, and the live page already shows them.
- Introduce a new part type `divider` distinct from `error`: required for clean differentiation; the existing `ErrorPart` shape is kept for terminal failures only.

### D5: `legacy-missing` fallback for sessions with no `mohist_prompt`

When `events.Count(e => e.Type == "mohist_prompt") == 0`:

- Emit exactly one turn.
- `user.kind = "legacy-missing"`, `user.role = "mohist"`, `user.sentAt = session.StartedAt ?? session.CreatedAt`, `user.text = "Prompt was not recorded for this historical session"` (or an equivalent server-localized string; the web `PromptBlock` already has a hard-coded rendering for `legacy-missing` that says the same thing).
- `user.summary` is omitted (or set with `kind: "legacy-missing"` and no `title`); the short task title may still appear in `metadata.title` for context, but it must never be copied into `user.text` or `user.summary.title` so the assertion "short task title does not stand in for the real prompt" holds in both directions.
- Every other event is still projected as a part inside that turn, so the rest of the transcript remains inspectable.

**Alternatives considered:**
- Synthesize a `mohist_prompt` from `session.Title` and proceed as if it were real: rejected because the spec explicitly forbids substituting session-level metadata for the real prompt and the acceptance criteria require a `legacy-missing` turn when there is no `mohist_prompt`.
- Return an empty transcript with a banner: rejected because reasoning / text / tool / terminal parts that do exist would be lost.

### D6: Web prompt card and tool disclosure align with the new payload, no new components

The frontend already has the right shape. Two alignments:

- `SessionTranscriptView.tsx` and `PromptBlock.tsx` already pass `turn.user.text` to `PromptSummaryCard.rawText` and to the `Copy` / `Show full prompt` controls. Once the backend stops substituting `session.Title` (D1, D5), no code change is needed in those components — `prompt.text` will already be the real `mohist_prompt.data.text`. We will add tests covering full-prompt expand and copy.
- The web `applyReasoningReorder` post-pass in `session-transcript-display.ts` is kept as a defensive measure for same-second reasoning-then-text blocks. It only moves blocks when the last reasoning and the following text share a `startedAt` second, so it does not interfere with the backend's interleaving guarantee; removing it is a future cleanup, not part of this change.
- `ToolCallCard` / `tool-registry.tsx` already surface `rawInput`, `rawOutput`, `metadata`, and `details` through the existing disclosure. The new backend keeps populating those fields (D3), so the disclosure keeps working without renderer changes.

**Alternatives considered:**
- Add a new "raw payload" drawer component to guarantee visibility: rejected because the existing tool disclosure already exposes those fields; we just need to assert that via test, not by adding a new surface.
- Remove `applyReasoningReorder`: deferred — it does not conflict with the new ordering and removing it without explicit prod evidence risks regressions on the live SSE path.

### D7: API surface stays the same; the `WorkflowAgentSessionTranscript` payload grows fields

`/api/issues/{number}/workflow/sessions/{sessionName}` and `/api/issues/{number}/coder-sessions/{sessionId}` continue to return a `WorkflowAgentSessionTranscript` with `turns`, `metadata`, and `workflowLogs`. The serialized shape gains:

- `turns[].user.title`, `turns[].user.outputPath`, `turns[].user.contextFiles` (when present on the `mohist_prompt` payload).
- `metadata.turnCount = turns.Length`, replacing the previous hard-coded `1`.

No new top-level fields, no new endpoints, no schema migration.

**Alternatives considered:**
- Add a `GET /api/issues/{n}/workflow/sessions/{name}/transcript/raw` that returns events verbatim: rejected — the live page already concatenates events with the projected transcript, and a separate raw endpoint would encourage consumers to bypass the canonical projection.

## Risks / Trade-offs

- [Many existing sessions have no `mohist_prompt` event recorded, so they will render as `legacy-missing`.] -> Mitigation: confirm the spec and product call for the explicit missing state; existing tests and live behavior on the affected sessions (e.g. `T-005.1`) already show the `mohist_prompt` payload is recorded, so the rollout unblocks the affected cases immediately. Sessions that genuinely lack the event get an honest missing-prompt state instead of misleading task-title text.
- [Closing reasoning / text streams on type switch can fragment long assistant messages that the agent re-opens after a tool call.] -> Mitigation: a new chunk of the same type resumes the existing part (we only close, not finalize-and-discard), so `agent_message_chunk → tool_call → agent_message_chunk` is two text parts, not three. This matches the live `useSessionTranscript` behavior where `appendTextToTurn` reuses the streaming part.
- [Adding `divider` parts increases the part list size and changes existing test fixtures.] -> Mitigation: extend the existing web and backend tests to assert the new part types and order; the existing tests already cover `legacy-missing` and the prompt card, so most assertions stay valid.
- [Backend iteration over every event per request is O(events).] -> Mitigation: events are already ordered and read once per request via the existing `AsNoTracking` query; the per-turn maps are small (one prompt + a handful of tools per turn in practice). No caching change is required for this change.
- [Frontend's `applyReasoningReorder` could re-order reasoning that the backend already placed correctly.] -> Mitigation: the post-pass only reorders a reasoning block when the immediately following text has a same-second `startedAt`; backend events almost never have a same-second reasoning-start/text-start split, so the post-pass is a no-op on backend-projected turns. It is kept as defense in depth and a future cleanup candidate.
- [Multiple `mohist_prompt` events in one session imply a real ACP session resume; if the runner is misbehaving, we will project more turns than the agent actually saw.] -> Mitigation: each `mohist_prompt` is emitted by `runPromptOnExistingWorkflowAgentSession` / `runResumedWorkflowAgentSession` / etc., and we trust that boundary. A `coder_recovery_status` retry that re-emits a `mohist_prompt` correctly produces a new turn, which is what the spec wants.
- [Rollback is simple but partial: reverting only the backend leaves the web tests that assert multiple turns failing, and reverting only the web loses the spec compliance.] -> Mitigation: deploy the backend and web together in one PR; the migration plan below couples them.

## Migration Plan

1. Backend (`packages/server/src/Mohist.Server/Sessions/Queries/WorkflowAgentSessionQueryService.cs`):
   - Replace the single-turn build inside `BuildTranscriptAsync` with a per-event loop that opens a new turn on `mohist_prompt`, populates `turn.user` from the payload, and feeds every other event into the current turn's `assistant` list in the order described in D1, D2, D3, D4.
   - Implement the `legacy-missing` fallback (D5): exactly one turn with `user.kind = "legacy-missing"` and no use of `session.Title` in `user.text`.
   - Extend `BuildAssistantParts` (or replace it with `BuildTurnParts` per turn) so that the per-turn accumulator pair closes on chunk-type change (D2), tool updates replace in place at the first-observed index (D3), and liveness / terminal / recovery events produce divider / error parts (D4).
   - Update `metadata.turnCount` to the actual number of `mohist_prompt` events (or 1 for `legacy-missing`).
2. Backend tests (`packages/server/tests/Mohist.Server.Tests/Specs/WorkflowSessionSpecs.cs`, `AgentSessionSpecs.cs`): add the seven scenarios from the proposal: full prompt text, no `session.Title` substitution, two `mohist_prompt` → two turns, `thought → tool → thought → text` ordering, tool update merging with first-position preservation, `legacy-missing` for sessions without `mohist_prompt`, and liveness / terminal projection.
3. Frontend (`packages/web/src/widgets/session-transcript/ui/SessionTranscriptView.tsx`, `PromptBlock.tsx`, `TurnList.tsx`, `model/session-transcript-display.ts`): no behavioral changes required; the existing components already pass `turn.user.text` to expand / copy. Keep `applyReasoningReorder` as defense in depth.
4. Frontend tests (`packages/web/tests/SessionPage.transcript.test.tsx`, `SessionPage.test.tsx`, `session-transcript-display.test.ts`): add tests that assert the prompt card expands and copies the full real `text` (not the title), that two `mohist_prompt` events render as two turns, that the `legacy-missing` state renders without a `Show full prompt` button, and that the tool card surfaces `rawInput` / `rawOutput` / `metadata` / `details`.
5. Verify against `/issues/8/workflow/sessions/T-005.1` (or its successor session) by re-querying `GET /api/issues/8/workflow/sessions/T-005.1` and confirming `turns[0].user.text` is the full `mohist_prompt.data.text`.

Rollback: revert the backend projection change and re-run the existing transcript tests; the web side requires no rollback because the components already accept the new payload shape. The `legacy-missing` branch is pure presentation and degrades to a single turn that says the prompt was not recorded.

## Open Questions

1. **Liveness event part shape.** The live web path projects `agent_liveness_status` and `coder_recovery_status` as `ErrorPart` of `kind: 'recovery'`. The backend should match that to keep live and replay visually identical, but the proposal also mentions "divider or status marker." We will pick `ErrorPart { kind: 'recovery', message: <liveness text> }` for both, and introduce a `DisplayDividerPart` only if the visible rendering needs to differ from an error part (decided during implementation by reading both the live handler and the acceptance scenarios).
2. **Defensive defaulting on `mohist_prompt.payload`.** Some historical `mohist_prompt` events recorded by earlier runner versions may have a missing `text` field (e.g. resume prompts that did not log the body). When `text` is missing, should we (a) still treat the event as a real `mohist_prompt` and project an empty `text` plus a `legacy-missing`-style banner, or (b) treat the event as legacy and fall back to a single `legacy-missing` turn? The runner's current `buildPromptEvent` always sets `text`, so option (a) is fine for new data; we will guard against null / empty `text` by using a labeled missing-prompt string and continue to treat the event as real so the per-prompt turn count is preserved.
3. **Should `applyReasoningReorder` be removed once the backend guarantees ordering?** Deferred. The post-pass is a no-op on backend-projected turns in practice; removing it without prod evidence risks regressing the live SSE path. Treat as a follow-up cleanup, not part of this change.
