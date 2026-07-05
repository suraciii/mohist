### Requirement: Transcript-based reductions reside in the transcript loading region

The two transcript-based reductions — event-summary batch computation (`LoadEventSummariesAsync`) and active-session reconciliation (`ReconcileActiveSessionsAsync`) — SHALL reside in the transcript loading region (the `TranscriptPartLoader` area or a peer type there) rather than on the core session query class as `internal static` members. The core query class (`AgentSessionQuerier`) SHALL NOT declare these reductions as `internal static` members after this change; it and the activity feed assembler SHALL delegate to the relocated reduction.

#### Scenario: Core query class exposes no transcript reduction statics

- **WHEN** the core session query service class is inspected after the change
- **THEN** it SHALL NOT declare `LoadEventSummariesAsync` or `ReconcileActiveSessionsAsync` as `internal static` members

### Requirement: Event-summary batch computation produces identical summaries after relocation

The relocated event-summary batch computation SHALL load transcript parts for the requested session ids, project them in sequence order, group them by session id (ordinal), and summarize each group via the transcript event summary projector, producing a session-id → `AgentSessionTranscriptSummary` dictionary. The result SHALL be byte-identical to the pre-change computation for every input, including empty-input and no-parts cases.

#### Scenario: Empty session-id input yields an empty dictionary

- **WHEN** the batch computation is invoked with no session ids
- **THEN** it SHALL return an empty dictionary (no parts are loaded)

#### Scenario: Sessions with no transcript parts are absent from the result

- **WHEN** the batch computation is invoked with session ids that have no transcript turns or parts
- **THEN** those session ids SHALL not appear as keys in the result dictionary

#### Scenario: Summaries are grouped and ordered identically to before

- **WHEN** the batch computation is invoked for sessions that carry transcript parts
- **THEN** each session id SHALL map to a summary computed over its parts projected and ordered by (sequence, id), identical to the pre-change projection, and shared with the activity feed assembler

### Requirement: Active-session reconciliation filters identically after relocation

The relocated active-session reconciliation SHALL retain the same filtering semantics: among the input sessions, only those bound to a runner (`AgentSessionId` is not null) are candidates for filtering; each candidate is validated against its workflow run by single-runner assignment and running-task work-id match, and sessions whose workflow run is absent or whose assignment has not yet been recorded are provisionally accepted. Non-active sessions and accepted active sessions SHALL pass through unchanged; the result ordering and membership SHALL be identical to the pre-change reconciliation.

#### Scenario: Non-active sessions always pass through

- **WHEN** reconciliation is invoked over a set that includes sessions with no bound `AgentSessionId`
- **THEN** those sessions SHALL appear in the result unchanged regardless of workflow-run state

#### Scenario: Active sessions are filtered against their workflow run

- **WHEN** reconciliation is invoked over active sessions whose workflow run exists and is assigned to a different runner, or whose running task does not match the session's work id
- **THEN** those sessions SHALL be excluded from the result, matching the pre-change filtering

#### Scenario: Unassigned workflow runs provisionally accept active sessions

- **WHEN** an active session's workflow run has no `AssignedTo` value
- **THEN** the session SHALL be retained, matching the pre-change provisional-acceptance rule

#### Scenario: Reconciliation result is shared by querier and activity feed

- **WHEN** both the core query service and the activity feed assembler reconcile the same set of sessions
- **THEN** they SHALL obtain identical results by invoking the same relocated reduction
