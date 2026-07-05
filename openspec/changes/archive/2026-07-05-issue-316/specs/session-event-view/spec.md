### Requirement: The event→view projection public surface stays importable from its current path

Every symbol exported today from `entities/session/model/view` SHALL remain importable from that exact module path with identical signatures after the split. The preserved surface comprises `viewSessionEvents` and the types `SessionEvent`, `SessionViewKind`, `SessionChatPart`, `SessionChatTurn`, `SessionChatView`, `SessionTimelineToolCall`, `SessionTimelineRecovery`, `SessionTimelineCompaction`, `SessionTimelineRound`, `SessionTimelineView`, `SessionCompactView`, and `SessionView`. The consumer `widgets/coder-session/model/useSessionTimeline.ts` SHALL require zero import-specifier changes, and `entities/session/model/view.test.ts` SHALL compile and pass without modification.

#### Scenario: The timeline-widget importer keeps compiling unchanged

- **WHEN** `widgets/coder-session/model/useSessionTimeline.ts` is type-checked after the split
- **THEN** `viewSessionEvents`, `SessionEvent`, `SessionTimelineToolCall`, `SessionTimelineRecovery`, and `SessionTimelineCompaction` SHALL all resolve from `../../../entities/session/model/view`
- **AND** no import specifier in that file SHALL change

#### Scenario: The existing view test suite passes unchanged

- **WHEN** `entities/session/model/view.test.ts` is executed against the refactored module
- **THEN** it SHALL compile without import edits
- **AND** every assertion SHALL pass

### Requirement: The three event→view projections produce identical output for any event sequence

For any `SessionEvent[]` input, `viewSessionEvents(events, kind)` SHALL return a result structurally and field-for-field identical to the pre-change implementation for each of the three kinds. Turn boundaries, part ordering, status mapping, tool-call merging, compaction attachment, recovery mapping, terminal-status resolution, preview truncation, and all counters SHALL be invariant.

#### Scenario: The chat projection is invariant across event shapes

- **WHEN** `viewSessionEvents(events, 'chat')` is invoked on event sequences covering empty input, input-first, assistant-first (legacy-missing turn), tool-only, interleaved reasoning/text deltas, `tool_call.*` status transitions, `session.closed` with failed/cancelled status, and `session.liveness` failures
- **THEN** the returned `SessionChatView` SHALL equal the pre-change output field by field, including turn ids, `incomplete` flags, `prompt.kind` resolution against `KNOWN_PROMPT_KINDS`, part ids derived from the per-turn counter, and `completedAt` set only when text/reasoning parts are closed

#### Scenario: The timeline projection is invariant across event shapes

- **WHEN** `viewSessionEvents(events, 'timeline')` is invoked on event sequences covering input-delimited rounds, synthesized empty rounds, compaction events (with and without an existing round), `tool_call.*` with completed/failed/cancelled vs. running status, and liveness recovery transitions
- **THEN** the returned `SessionTimelineView` SHALL equal the pre-change output field by field, including `roundIndex` sequencing, `toolCalls` derived from the per-round `toolCallMap` (with in-place mutation semantics for existing entries), `recovery` status mapping (`probing`→`recovering`, `running`→`recovered`, `failed`→`failed`, else `detected`), and `compactions` records with nullable `contextWindow*` fields

#### Scenario: The compact projection is invariant across event shapes

- **WHEN** `viewSessionEvents(events, 'compact')` is invoked on event sequences covering empty input, prompts, assistant text/reasoning chunks, tool events with and without `toolCallId`, `session.closed`, and liveness failures
- **THEN** the returned `SessionCompactView` SHALL equal the pre-change output field by field, including `toolCount` deduplication via `seenToolCallIds`, `preview` truncation at 200 characters with the `…` suffix, default `terminalStatus` of `running`, and `failureReason` present only when a failed/cancelled terminal status is reached

#### Scenario: The dispatcher routes by kind and tags the result

- **WHEN** `viewSessionEvents(events, kind)` is invoked with `kind` being `'chat'`, `'timeline'`, or `'compact'`
- **THEN** it SHALL route to the corresponding projector
- **AND** the returned view's `kind` discriminator SHALL equal the requested `kind`

### Requirement: view.ts is decomposed into one file per projector plus a shared helpers module

The three projectors `buildChatView`, `buildTimelineView`, and `buildCompactView` SHALL each live in its own file — `entities/session/model/view/chat.ts`, `view/timeline.ts`, and `view/compact.ts` respectively. The shared event-parsing utilities (`narrowPayload`, `isRecord`, `getStringProp`, `getNumberProp`, `extractTextChunk`, `normalizeRaw`, `normalizeToolName`, `mapToolState`, `mapTerminalStatus`, the `is*Event` predicates, `defaultToolStatus`, `toolRecord`, `readToolString`, `readToolValue`) SHALL live in `entities/session/model/view/helpers.ts`. Chat-specific builders (`nextPartId`, `appendTextPart`, `appendReasoningPart`, `closeOpenTextParts`, `upsertToolPart`, `pushErrorPart`, `makeInitialTurn`, `makePromptTurn`, and the `KNOWN_PROMPT_KINDS` set) SHALL live in `view/chat.ts`. `view.ts` SHALL remain as a thin public entry point that re-exports the view types and the `viewSessionEvents` dispatcher, and SHALL NOT contain any projector implementation or shared helper body.

#### Scenario: Each projector resides in its own file

- **WHEN** the `entities/session/model/view/` directory is inspected
- **THEN** `chat.ts`, `timeline.ts`, `compact.ts`, and `helpers.ts` SHALL each exist as independent files
- **AND** `buildChatView` SHALL be defined in `chat.ts`, `buildTimelineView` in `timeline.ts`, and `buildCompactView` in `compact.ts`
- **AND** none of these projector implementations SHALL remain in `view.ts`

#### Scenario: Shared event-parsing helpers are centralized

- **WHEN** any projector needs an event-parsing helper (for example `narrowPayload`, `extractTextChunk`, or `isInputEvent`)
- **THEN** it SHALL import that helper from `view/helpers.ts`
- **AND** the helper body SHALL NOT be duplicated across projector files

#### Scenario: Chat-only builders move with the chat projector

- **WHEN** `view/chat.ts` is inspected
- **THEN** `makePromptTurn`, `upsertToolPart`, `appendTextPart`, `appendReasoningPart`, `closeOpenTextParts`, `pushErrorPart`, `makeInitialTurn`, `nextPartId`, and `KNOWN_PROMPT_KINDS` SHALL reside there
- **AND** the timeline and compact projectors SHALL NOT carry these chat-only builders

#### Scenario: view.ts remains the public entry point

- **WHEN** `view.ts` is inspected
- **THEN** it SHALL re-export `viewSessionEvents` and every view type listed in the public-surface requirement
- **AND** it SHALL contain only the kind-based dispatch and re-exports

### Requirement: Each file in the view module leaves the complexity hotspot band

After the split, no single file under `entities/session/model/view/` (including `view.ts` and every `view/*.ts`) SHALL remain in the `C=180+` hotspot band that `view.ts` occupied before the change (C=251). The combined complexity SHALL be distributed across the per-projector and helpers files so each projector is independently editable without touching the others.

#### Scenario: view.ts drops out of the hotspot band

- **WHEN** SCC complexity is measured on `entities/session/model/view.ts`
- **THEN** its complexity SHALL fall below the `C=180` hotspot threshold

#### Scenario: No split file re-enters the hotspot band

- **WHEN** SCC complexity is measured on each of `view/chat.ts`, `view/timeline.ts`, `view/compact.ts`, and `view/helpers.ts`
- **THEN** each SHALL sit below the `C=180` hotspot threshold
