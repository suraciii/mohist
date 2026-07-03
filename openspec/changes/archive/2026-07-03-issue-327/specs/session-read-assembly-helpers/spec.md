### Requirement: Transcript turns/parts loading is defined in exactly one place

The transcript loading sequence — query transcript turns for the given session(s), collect their turn ids, query transcript parts for those turn ids, and build a session-id-by-turn-id dictionary — SHALL be defined in a single shared method. All call sites that currently inline or duplicate this sequence (latest-event loading, event-summary loading, terminal-fact loading, generic-session summary inline projection, and session-metadata inline projection) SHALL delegate to this single shared method. No call site SHALL inline its own copy of the turns → turnIds → parts → sessionByTurnId-dictionary sequence after this change.

#### Scenario: All transcript-loading call sites share one implementation

- **WHEN** the session read-side services are inspected after the change
- **THEN** the turns → turnIds → parts → sessionByTurnId-dictionary loading sequence SHALL appear exactly once in the codebase
- **AND** every former duplication site (latest-event loading, event-summary loading, terminal-fact loading, generic-session summary, session-metadata builder) SHALL call the shared method

#### Scenario: Latest-event loading produces identical results

- **WHEN** the latest transcript event is loaded for a set of sessions after consolidation
- **THEN** the resulting per-session `TranscriptEventProjection` dictionary SHALL be identical to the pre-consolidation result (same ordering by `LastSeenAt` then `Id`, same last-wins semantics per session)

#### Scenario: Event-summary loading produces identical results

- **WHEN** transcript event summaries are loaded for a set of sessions after consolidation
- **THEN** the resulting per-session `AgentSessionTranscriptSummary` dictionary SHALL be identical to the pre-consolidation result

#### Scenario: Terminal-fact loading produces identical results

- **WHEN** terminal facts are loaded for a set of sessions after consolidation
- **THEN** the resulting per-session `TerminalFact` dictionary SHALL be identical to the pre-consolidation result (same closure-part filtering, same ordering, same last-wins semantics)

#### Scenario: Generic session summary transcript projection is unchanged

- **WHEN** a generic session summary is built after consolidation
- **THEN** the transcript events projected from the shared loader SHALL produce the same resolved model, failure category, tool-call count, and tool-error count as before

#### Scenario: Session metadata transcript projection is unchanged

- **WHEN** session metadata is built after consolidation
- **THEN** the part count, tool count, and event summary SHALL be identical to the pre-consolidation values

### Requirement: Context-reference envelope construction is defined in exactly one place

The two methods that construct a context-reference envelope from the same four launch labels (`IssueNumber`, `EpicNumber`, `Repository`, `WorkspacePath`) with identical null-when-all-empty semantics — `BuildAgentSessionListContextRefs` (returning `AgentSessionListContextRefsDto`) and `BuildGenericSessionSummaryContextRefs` (returning `GenericAgentSessionSummaryContextRefsDto`) — SHALL be merged into a single shared construction site. The shared construction SHALL read the same four labels, parse the issue number identically, and return null when all four are absent. The two distinct DTO return types (`AgentSessionListContextRefsDto` and `GenericAgentSessionSummaryContextRefsDto`) SHALL continue to exist as independent wire shapes; only the construction logic is shared.

#### Scenario: Both context-ref call sites share one construction implementation

- **WHEN** the session read-side services are inspected after the change
- **THEN** the four-label context-reference construction logic (read issue-number label, parse to int-or-null, read epic/repository/workspace labels, return null when all absent) SHALL appear exactly once in the codebase
- **AND** both the agent-scoped list and the generic-session summary SHALL delegate to it

#### Scenario: Agent-scoped list context-refs are unchanged

- **WHEN** the agent-scoped session list endpoint returns sessions after consolidation
- **THEN** the `contextRefs` envelope on each `AgentSessionListItemDto` SHALL be identical to the pre-consolidation value (null when no context references, populated when any are present)

#### Scenario: Generic session summary context-refs are unchanged

- **WHEN** the generic session summary endpoint returns a session after consolidation
- **THEN** the `contextRefs` envelope on the `GenericAgentSessionSummaryDto` SHALL be identical to the pre-consolidation value

### Requirement: Observable behavior of all read-side consumers is preserved

The consolidation of transcript loading and context-ref construction is a pure internal refactor. No HTTP response, DTO field value, status resolution, ordering, or nullability SHALL change as a result. All existing session-related specs SHALL pass without regression.

#### Scenario: All session-related specs pass

- **WHEN** the full server test suite is run after consolidation
- **THEN** all existing session-related specs SHALL pass with no regressions
