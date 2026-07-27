### Requirement: Project feed uses the persisted terminal activity type
The project event feed SHALL expose each eligible terminal AgentSession activity fact as a `session.activity` event. The feed MUST preserve the Session source and subject, persisted event time, terminal status, and available failure context from the underlying fact.

#### Scenario: Completed Session activity appears in the project feed
- **WHEN** a Project Session persists a terminal `session.activity` fact with a completed status
- **THEN** the project event feed SHALL contain a Session event of type `session.activity` with that terminal status and the originating Session context

#### Scenario: Failed Session activity appears in the project feed
- **WHEN** a Project Session persists a terminal `session.activity` fact with failed, timeout, or cancelled status and failure context
- **THEN** the project event feed SHALL expose it as `session.activity` and retain the available failure context for attention rendering

### Requirement: Issue feed uses the persisted terminal activity type
The issue event feed SHALL expose an eligible routed AgentJob terminal failure as a `session.activity` event. It MUST preserve the existing routed-session eligibility, issue and trigger lineage, Session and Agent identity, terminal status, and failure details.

#### Scenario: Routed AgentJob failure appears in the issue feed
- **WHEN** a routed AgentJob-owned Session persists an eligible failed terminal `session.activity` fact for an Issue
- **THEN** the Issue event feed SHALL expose exactly one `session.activity` event with the existing Session, Agent, trigger, Issue, and failure context

### Requirement: Web presents terminal Session activity consistently
The Web activity feed SHALL recognize `session.activity` terminal context as an AgentSession event. It SHALL display the reported terminal status for routine terminal outcomes and SHALL present failed, timeout, and cancelled outcomes with failure attention while retaining the existing navigation targets.

#### Scenario: Routine terminal activity is displayed
- **WHEN** the activity feed receives a `session.activity` event with a completed terminal status
- **THEN** it SHALL present the Session as completed and retain its Session navigation target

#### Scenario: Failed terminal activity is displayed
- **WHEN** the activity feed receives a `session.activity` event with failed status and a failure reason or category
- **THEN** it SHALL present the event with failure attention and show the available failure detail
