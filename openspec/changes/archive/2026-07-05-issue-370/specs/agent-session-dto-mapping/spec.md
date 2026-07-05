### Requirement: Session DTO projections reside in a dedicated mapper independent of the query service

The Session DTO projections — usage (`ToUsageDto` for both `AgentUsageSummary` and `AgentSession` overloads, including the bounded `ContextUsageHistory` trend projection), event summary (`ToEventSummaryDto`), runtime session lineage (`BuildLineageDto`), context usage history (`BuildUsageHistoryDto`), and transcript-event projection (`ToProjection`) — SHALL live in a mapper type that is separate from the core session query class (`AgentSessionQuerier`). The core query class SHALL NOT declare these projection methods as `internal static` members after this change; it SHALL delegate to the dedicated mapper.

#### Scenario: Core query class exposes no DTO projection statics

- **WHEN** the core session query service class is inspected after the change
- **THEN** it SHALL NOT declare `ToUsageDto`, `ToEventSummaryDto`, `BuildLineageDto`, `BuildUsageHistoryDto`, or `ToProjection` as `internal static` members, and its private DTO construction SHALL call the dedicated mapper

### Requirement: All consumers invoke the same mapper methods for identical projections

The consumers of Session DTO projections SHALL obtain each real shared projection by calling the same mapper method rather than each holding its own copy: the core query service and activity feed assembler share usage and event-summary projections, the session metadata path uses the shared lineage projection, and transcript-event callers use the shared transcript projection. The same input SHALL produce the same output across applicable consumer surfaces, preserving the byte-alignment invariant that previously lived only in prose comments.

#### Scenario: Activity feed and core query share the usage projection

- **WHEN** the activity feed assembler and the core query service each project usage for an identical `AgentSession`
- **THEN** both SHALL produce an identical `AgentUsageDto` by invoking the same mapper method, including the context-window percentage, context-health classification, and usage-history trend entries

#### Scenario: Activity feed and core query share the event-summary projection

- **WHEN** the activity feed assembler and the core query service each project an identical `AgentSessionTranscriptSummary`
- **THEN** both SHALL produce an identical `AgentEventSummaryDto` by invoking the same mapper method, including resolved model, failure category, context-exhaustion flags, and tool call/error counts

#### Scenario: Metadata lineage projection matches the shared mapper

- **WHEN** the session metadata builder projects runtime lineage for an `AgentSession`
- **THEN** it SHALL produce the same `RuntimeSessionLineageEntryDto` list (or `null`) as the shared mapper method, including the legacy single-binding synthesis fallback

### Requirement: DTO projection output is byte-identical to the pre-change output

Each mapper method SHALL produce a DTO that is byte-for-byte identical to the DTO the corresponding projection produced before this change, for every field including token counts, cost amount/currency, context-window size/used/percent, context-health classification, failure category, context-exhaustion boolean flags (which remain `null` rather than `false` when not matched), tool counts, lineage entry ids and ISO timestamps, and usage-history trend points (including the null-when-empty rule for historical sessions).

#### Scenario: Usage DTO fields are unchanged

- **WHEN** the usage mapper is invoked with a session that has recorded usage and context-usage history
- **THEN** the resulting `AgentUsageDto` SHALL carry identical input/output/total/cached-read/thought tokens, cost amount/currency, context-window used/size/percent, context-health classification, and history entries to the pre-change projection

#### Scenario: Event summary flags remain null when unmatched

- **WHEN** the event-summary mapper is invoked with a summary whose failure category is neither context-exhaustion nor suspected-context-exhaustion
- **THEN** the context-exhaustion and suspected-context-exhaustion flags SHALL be `null` (not `false`), matching the pre-change projection

#### Scenario: Usage history projects null for empty or absent history

- **WHEN** the usage-history mapper is invoked with a session whose `ContextUsageHistory` is null or empty
- **THEN** the projection SHALL return `null` so the wire stays quiet for historical/legacy sessions, matching the pre-change rule

### Requirement: Transcript-event projection centralizes the text/reasoning payload rewrite

The transcript-event projection (`ToProjection`) SHALL continue to rewrite the payload of `text` and `reasoning` part types to a serialized `{ text }` object while passing through other part types' raw payload, so both the latest-event loader and the event-summary batch path observe identical projected events. This projection SHALL be provided by the dedicated mapper.

#### Scenario: Text and reasoning parts are normalized identically across callers

- **WHEN** a `text` or `reasoning` transcript part is projected by any caller through the mapper
- **THEN** the projected `PayloadJson` SHALL be the serialized `{ text = part.Text }` object, and all other part types SHALL carry their verbatim `PayloadJson`
