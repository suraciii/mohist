### Requirement: Durable Session Event Summary
Each persisted AgentSession SHALL retain the event summary used by its activity card. When accepted runtime observations change the summary, the updated summary MUST be persisted with the Session state; a Session with no applicable observations MUST expose an empty summary.

#### Scenario: Runtime observations update the stored summary
- **WHEN** an AgentSession receives accepted model-resolution, tool, and terminal activity observations
- **THEN** its persisted summary MUST expose the resolved model, distinct tool-call count, failed-tool-call count, and the failure category from the latest terminal activity fact

#### Scenario: Latest terminal fact remains internally consistent
- **WHEN** multiple terminal activity observations exist for an AgentSession, including observations from separate turns
- **THEN** the persisted failure category and failure reason MUST both be derived from the same latest terminal activity fact in turn, part, and identifier order

#### Scenario: Repeated tool observations are not double counted
- **WHEN** an AgentSession receives multiple observations for the same tool-call identifier, including a failed observation
- **THEN** its persisted summary MUST count that identifier once as a tool call and once as a failed tool call

### Requirement: Activity Feed Uses Persisted Session Summaries
`GET /api/projects/{projectRef}/agent/activity` and `GET /api/agent/activity` SHALL project each returned Session card's `eventSummary` from the persisted AgentSession summary. The endpoints MUST preserve their existing response schema, selected-card ordering and limit behavior, activity status, agent attribution, issue title, work item, task progress, usage, waiting cards, and latest-activity preview behavior.

#### Scenario: Activity card exposes the stored event summary
- **WHEN** an activity request returns a Session whose persisted summary contains a resolved model, failure category, and tool counts
- **THEN** that Session's card MUST expose equivalent values in `eventSummary`

#### Scenario: Canonical and alias routes remain equivalent
- **WHEN** the same resolved project and activity limit are requested through the canonical route and the alias route
- **THEN** both responses MUST contain equivalent activity data, including each card's `eventSummary`

### Requirement: Activity Summary Reads Are Transcript-Bounded
The activity-feed read path MUST NOT load or reduce transcript turns or parts to construct a Session's `eventSummary`; it SHALL use the persisted summary. The `amplification.transcriptRecords` value MUST count only transcript records actually materialized for remaining activity-card projections and MUST NOT include records that would otherwise have been read solely to construct event summaries.

#### Scenario: Historical transcript growth does not add summary-read work
- **WHEN** additional transcript parts are persisted for a returned Session without changing its persisted event summary
- **THEN** a subsequent activity request MUST return the same `eventSummary` without materializing those additional parts to rebuild that summary, and its amplification accounting MUST exclude them from summary-read work

#### Scenario: Empty activity feed performs no transcript summary read
- **WHEN** an activity request selects no Session cards
- **THEN** the response MUST contain an empty Session list and `amplification.transcriptRecords` MUST be zero
