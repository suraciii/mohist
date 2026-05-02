## ADDED Requirements

### Requirement: Coder session title stored on creation
The `coder_session` table SHALL include a nullable `title TEXT` column. When a caller passes a `title` value via `AcpSessionOptions.title` or `AcpConnectionOptions.title`, the system SHALL persist it in the `coder_session` row at insert time. Both `runAcpSession` and `createAcpConnection` code paths SHALL accept and store `title`.

#### Scenario: Session created with title via runAcpSession
- **WHEN** `runAcpSession` is called with `title: "T-004: Create Plan and CheckStageRunner"`
- **THEN** the `coder_session` row has `title = "T-004: Create Plan and CheckStageRunner"`

#### Scenario: Session created with title via createAcpConnection
- **WHEN** `createAcpConnection` is called with `title: "Plan stage"`
- **THEN** the `coder_session` row has `title = "Plan stage"`

#### Scenario: Session created without title
- **WHEN** a caller does not pass `title` in options
- **THEN** the `coder_session` row has `title = NULL`

### Requirement: Each caller supplies a descriptive session title
All callers that create coder sessions SHALL supply a human-readable `title`:

| Caller | Title format |
|--------|-------------|
| RalphExecutor | `{task.id}: {task.title}` (e.g. `T-004: Create Plan and CheckStageRunner`) |
| PlanStageRunner | `Plan stage` |
| CheckStageRunner | `Check stage` |
| CodeCompilesCheck | `Auto-fix: compilation errors` |
| BuildTestCheck | `Auto-fix: test failures` |
| SkillService | `Skill: {skill.name}` |
| ExploreACPService | `Explore: {issue.title}` |

#### Scenario: RalphExecutor passes task title
- **WHEN** RalphExecutor runs task T-004 with title "Create Plan and CheckStageRunner"
- **THEN** it calls `runAcpSession` with `title: "T-004: Create Plan and CheckStageRunner"`

#### Scenario: SkillService passes skill name
- **WHEN** SkillService runs skill named "debug-helper"
- **THEN** it calls `runAcpSession` with `title: "Skill: debug-helper"`

#### Scenario: ExploreACPService passes issue title
- **WHEN** ExploreACPService explores issue titled "Fix login bug"
- **THEN** it calls `runAcpSession` with `title: "Explore: Fix login bug"`

### Requirement: SSE coder_session_started event carries title
The `coder_session_started` SSE event payload SHALL include the session's `title` field (nullable string).

#### Scenario: Session started event with title
- **WHEN** a coder session is created with `title: "Plan stage"`
- **THEN** the `coder_session_started` SSE event payload includes `title: "Plan stage"`

#### Scenario: Session started event without title (legacy)
- **WHEN** a coder session is created without a title
- **THEN** the `coder_session_started` SSE event payload includes `title: null`

### Requirement: API responses include session title
The `GET /api/issues/:number/coder-sessions` and `GET /api/agent/sessions` endpoints SHALL return the `title` field on each session object.

#### Scenario: Issue coder sessions response includes title
- **WHEN** `GET /api/issues/42/coder-sessions` returns sessions
- **THEN** each session object includes a `title` field (string or null)

#### Scenario: Agent sessions response includes title
- **WHEN** `GET /api/agent/sessions` returns sessions
- **THEN** each session object includes a `title` field (string or null)

### Requirement: Frontend displays session title with priority fallback chain
The frontend SHALL display a session label using the following priority: (1) `session.title` if present, (2) taskId parsed from `executionId`, (3) stage name, (4) first 24 characters of `taskDescription`.

#### Scenario: Session with title displays title
- **WHEN** a session has `title: "T-004: Create Plan and CheckStageRunner"`
- **THEN** the UI displays "T-004: Create Plan and CheckStageRunner"

#### Scenario: Session without title falls back to taskId
- **WHEN** a session has `title: null` and `executionId: "build-127-T-004"`
- **THEN** the UI displays "T-004" (parsed from executionId)

#### Scenario: Session without title or taskId falls back to stage
- **WHEN** a session has `title: null`, no parseable executionId, and `stage: "Plan"`
- **THEN** the UI displays "Plan"

#### Scenario: All fallbacks exhausted uses taskDescription
- **WHEN** a session has `title: null`, no executionId, no stage, and `taskDescription: "Implement the feature that..."`
- **THEN** the UI displays "Implement the feature tha" (first 24 chars)
