### Requirement: The session detail page has exactly one data path

The web session detail page SHALL obtain all of its session data from a single data source: the unified session data hook `useUnifiedSessionDataSource` in `pages/session/data`. The page SHALL pass that hook's result to `SessionDetailShell` as its only data input. Session summary, transcript, followup, and turn-control data MUST NOT be loaded through any other hook or client call in the session detail path.

#### Scenario: Rendering the session detail page

- **WHEN** a session detail route renders
- **THEN** the page SHALL call `useUnifiedSessionDataSource` and pass the result as the `data` prop of `SessionDetailShell`
- **AND** the session summary and transcript SHALL be loaded by the unified session queries inside that hook

#### Scenario: Session detail data is needed outside the hook

- **WHEN** session detail code needs summary, transcript, followup, or stop data for the rendered session
- **THEN** it SHALL read it from the unified data source result instead of issuing a parallel query or client call

### Requirement: The hook's inferred return type is the data contract

The contract between `pages/session/data` and `SessionDetailShell` SHALL be the concrete return type inferred from `useUnifiedSessionDataSource`'s implementation. `pages/session/data` MUST NOT maintain a hand-written interface that re-declares the hook's result shape, including the former `SessionDataSourceResult` and `SessionTurnControlHandle` interfaces. The contract SHALL contain exactly the fields the hook returns; fields that exist only in a parallel declaration and that no consumer reads (such as the runtime-lineage fields) MUST NOT appear in the contract, and fields whose only producer fills them with a constant empty value MUST NOT be returned, destructured, or rendered.

#### Scenario: The shell types its data prop from the hook

- **WHEN** `SessionDetailShell` or its tests declare the type of the `data` prop or of values derived from it, such as the stop handle
- **THEN** the type SHALL be derived from `useUnifiedSessionDataSource`'s inferred return type
- **AND** removing a field from the hook's return object SHALL become a compile-time error wherever that field is still read

#### Scenario: No hand-maintained duplicate of the contract remains

- **WHEN** the modules under `pages/session/data` are inspected after this change
- **THEN** no standalone interface re-declaring the hook's result or turn-control shape SHALL exist
- **AND** fields the hook never returns SHALL be absent from the shell's data contract

#### Scenario: Always-empty fields and their render branches are gone

- **WHEN** the hook's return object and the session detail shell are inspected after this change
- **THEN** no field the hook fills with a constant empty value (the former `siblingNav`, `siblingSidebar`, `issueTitle`) SHALL remain in the hook's return, the shell's destructuring, or the shell's header props
- **AND** the shell SHALL render no empty branch for them, including the narrow-viewport sibling-navigation slot and the sibling-sidebar slot

### Requirement: Unified session clients are the only session-detail data clients

Web SHALL keep exactly one client family for loading session detail data: the unified session summary and transcript clients and hooks in `entities/coder-session` (`getUnifiedSessionSummary`, `getUnifiedSessionTranscript`, `unifiedSessionSummaryQueryOptions`, `unifiedSessionTranscriptQueryOptions`, `useUnifiedSessionSummary`, `useUnifiedSessionTranscript`). The issue-scoped client functions `getCoderSessions`, `getAgentSessionMetadata`, `getAgentSessionTranscript`, and `getAgentSessionEvents` MUST NOT exist, and the duplicate generic-session clients in `entities/agent` (`getGenericSessionSummary`, `getGenericSessionTranscript`, their query options and hooks, and the `GenericAgentSessionSummaryDto` type) MUST NOT exist.

#### Scenario: Loading a session's summary and transcript

- **WHEN** the unified data source loads a session
- **THEN** the summary SHALL be requested from the unified session summary endpoint scoped by project and session id
- **AND** the transcript SHALL be requested from the unified session transcript endpoint with the runtime session id and the public/raw view as parameters

#### Scenario: Removed clients leave no references

- **WHEN** the web codebase is searched for the removed client functions, hooks, query options, or DTO type
- **THEN** no production module or test SHALL export, import, or invoke them

### Requirement: Followup and turn control flow through the generic operations

The unified data source SHALL expose followup submission and turn stop through the generic agent-session operations. Submitting a followup SHALL post through the generic followup operation with an idempotency key that is reused when an identical submission (same session, text, and attachments) is retried. Stopping SHALL be exposed as a turn-control handle that exists only while the session's current turn is queued or executing. The parallel issue-scoped mutations (`useFollowupMutation` with `postFollowup`, and `useStopSessionMutation` with `stopSession`) MUST NOT exist in web.

#### Scenario: Submitting a followup

- **WHEN** the user submits followup text, optionally with attachments
- **THEN** the data source SHALL submit it through the generic followup operation carrying an idempotency key
- **AND** an identical retry SHALL reuse the same idempotency key

#### Scenario: Stopping the current turn

- **WHEN** the session summary reports a current turn whose status is queued or executing
- **THEN** the data source SHALL expose a stop handle carrying that turn's id and state whose mutate operation issues the generic turn-control stop
- **WHEN** no current turn is queued or executing
- **THEN** the stop handle SHALL be null

### Requirement: Dead session data-layer code is removed while surviving clients stay intact

Session data-layer code whose only consumers are the removed paths MUST NOT remain in web: the issue-scoped session list hook `useCoderSessions`, the activity helpers `canRecoverSession` and `deriveSessionActivity`, and `buildGenericSessionMetadata` in `pages/session/data` SHALL be deleted, and slice public APIs (`index.ts`) SHALL NOT export removed symbols. Session data clients with production consumers SHALL remain functional, including workflow-run sessions (`useWorkflowRunSessions`), session recovery compact and reset for both issue-scoped and generic sessions, and the activity helpers used by the unified data source (`canFollowupSession`, `deriveSessionStatusKind`). Tests that cover only deleted code SHALL be removed with their subjects.

#### Scenario: Pruned public APIs

- **WHEN** another slice imports from `entities/coder-session`, `entities/agent`, or `pages/session/data`
- **THEN** every removed symbol SHALL be absent from the slice's public API
- **AND** every surviving symbol, including the unified session clients and the recovery compact and reset clients, SHALL still resolve

#### Scenario: Recovery actions keep working

- **WHEN** the session detail page offers compact or reset for a session that is not running
- **THEN** the action SHALL still be issued through the surviving session recovery clients in `entities/coder-session`

#### Scenario: Tests cover only surviving code

- **WHEN** the web test suite runs after this change
- **THEN** no test SHALL import or exercise a deleted module, hook, client function, or helper
