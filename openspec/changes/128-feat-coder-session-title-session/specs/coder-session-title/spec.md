## ADDED Requirements

### Requirement: coder_session table has title column
The `coder_session` table SHALL include a `title TEXT` column. The column SHALL be nullable to maintain backward compatibility with existing rows.

#### Scenario: New session row with title
- **WHEN** a coder_session row is inserted with `title: "T-004: Create Plan and CheckStageRunner"`
- **THEN** the row is stored with `title` = `"T-004: Create Plan and CheckStageRunner"`

#### Scenario: New session row without title
- **WHEN** a coder_session row is inserted without a title
- **THEN** the row is stored with `title` = `NULL`

#### Scenario: Existing rows unaffected by migration
- **WHEN** the migration adds the `title` column
- **THEN** all existing coder_session rows have `title` = `NULL`
- **AND** no data is lost or corrupted

### Requirement: CreateCoderSessionData accepts title
The `CreateCoderSessionData` interface SHALL include an optional `title?: string` field. The `CoderSessionRepo.insert()` method SHALL persist the `title` value when provided.

#### Scenario: Insert with title
- **WHEN** `coderSessionRepo.insert({ issueId, acpSessionId, title: "Plan stage" })` is called
- **THEN** the returned `CoderSession` has `title: "Plan stage"`

#### Scenario: Insert without title
- **WHEN** `coderSessionRepo.insert({ issueId, acpSessionId })` is called
- **THEN** the returned `CoderSession` has `title: null`

### Requirement: Callers pass meaningful session titles
Each caller of `runAcpSession` or `createAcpConnection` SHALL pass a `title` in the options:

| Caller | Interface | Title pattern |
|--------|-----------|---------------|
| RalphExecutor (per-task) | runAcpSession | `${task.id}: ${task.title}` |
| PlanStageRunner | createAcpConnection | `"Plan stage"` |
| CheckStageRunner | createAcpConnection | `"Check stage"` |
| CodeCompilesCheck (auto-fix) | runAcpSession | `"Auto-fix: compilation errors"` |
| BuildTestCheck (auto-fix) | runAcpSession | `"Auto-fix: test failures"` |
| SkillService | runAcpSession | `` `Skill: ${skill.name}` `` |
| ExploreACPService | runAcpSession | `` `Explore: ${issue.title}` `` |
| ConflictResolution | createAcpConnection | `"Conflict resolution"` |
| Server build fix | createAcpConnection | `"Auto-fix: build errors"` |

#### Scenario: RalphExecutor creates Build task session
- **WHEN** RalphExecutor runs task T-004 with title "Create Plan and CheckStageRunner"
- **THEN** `runAcpSession` is called with `title: "T-004: Create Plan and CheckStageRunner"`

#### Scenario: PlanStageRunner creates Plan session
- **WHEN** PlanStageRunner creates an ACP connection for Plan stage
- **THEN** `createAcpConnection` is called with `title: "Plan stage"`

#### Scenario: CheckStageRunner creates Check session
- **WHEN** CheckStageRunner creates an ACP connection for Check stage
- **THEN** `createAcpConnection` is called with `title: "Check stage"`

#### Scenario: CodeCompilesCheck auto-fix session
- **WHEN** CodeCompilesCheck runs an auto-fix session for compilation errors
- **THEN** `runAcpSession` is called with `title: "Auto-fix: compilation errors"`

#### Scenario: BuildTestCheck auto-fix session
- **WHEN** BuildTestCheck runs an auto-fix session for test failures
- **THEN** `runAcpSession` is called with `title: "Auto-fix: test failures"`

#### Scenario: SkillService session
- **WHEN** SkillService runs a skill named "walkthrough"
- **THEN** `runAcpSession` is called with `title: "Skill: walkthrough"`

#### Scenario: ExploreACPService session
- **WHEN** ExploreACPService runs for issue titled "Add login page"
- **THEN** `runAcpSession` is called with `title: "Explore: Add login page"`

#### Scenario: ConflictResolution session
- **WHEN** ConflictResolution creates an ACP connection to resolve merge conflicts
- **THEN** `createAcpConnection` is called with `title: "Conflict resolution"`

#### Scenario: Server build fix session
- **WHEN** the server auto-fix handler creates an ACP connection to fix build errors
- **THEN** `createAcpConnection` is called with `title: "Auto-fix: build errors"`

### Requirement: API endpoints return title field
The `GET /api/issues/:number/coder-sessions` endpoint SHALL include `title` in the returned session objects. The `GET /api/agent/sessions` endpoint SHALL include `title` in the returned session objects via `findAllWithIssueInfo`.

#### Scenario: Issue coder-sessions includes title
- **WHEN** `GET /api/issues/5/coder-sessions` is called
- **THEN** each session object in the response includes `title: string | null`

#### Scenario: Agent sessions list includes title
- **WHEN** `GET /api/agent/sessions` is called
- **THEN** each session object in the response includes `title: string | null`

### Requirement: Frontend displays session title with fallback chain
The frontend SHALL display a human-readable label for each coder session using the following priority:
1. `session.title` — use directly if non-null
2. Parse `taskId` from `executionId` — `build-127-T-004` → display `"T-004"`
3. `stage` name — display as-is (e.g., `"Plan"`, `"Check"`)
4. `taskDescription` first 24 characters — fallback (existing behavior)

#### Scenario: Session with title displays title
- **WHEN** a session has `title: "T-004: Create Plan and CheckStageRunner"`
- **THEN** the frontend displays `"T-004: Create Plan and CheckStageRunner"`

#### Scenario: Session without title falls back to executionId
- **WHEN** a session has `title: null` and `executionId: "build-127-T-004"`
- **THEN** the frontend extracts and displays `"T-004"`

#### Scenario: Session without title or parseable executionId falls back to stage
- **WHEN** a session has `title: null` and `executionId: "plan-5"` and `stage: "Plan"`
- **THEN** the frontend displays `"Plan"`

#### Scenario: Session with no identifiers falls back to taskDescription
- **WHEN** a session has `title: null`, `executionId: null`, `stage: null`, and `taskDescription: "<mohist-task>\n\n<role>\nYou ar"`
- **THEN** the frontend displays `taskDescription` truncated to 24 characters
