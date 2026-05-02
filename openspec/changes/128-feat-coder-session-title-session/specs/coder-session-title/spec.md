## ADDED Requirements

### Requirement: coder_session table has title column
The `coder_session` table SHALL include a `title TEXT` column. The column SHALL be nullable to maintain backward compatibility with existing rows.

#### Scenario: New session created with title
- **WHEN** a coder_session row is inserted with `title: "T-004: Create Plan"`
- **THEN** the row is stored with the title value in the `title` column

#### Scenario: Existing session without title
- **WHEN** a coder_session row exists from before this feature
- **THEN** the `title` column value is `NULL`

### Requirement: CreateCoderSessionData includes title
The `CreateCoderSessionData` interface SHALL include an optional `title?: string` field. When provided, it SHALL be stored in the `title` column on insert.

#### Scenario: Insert with title
- **WHEN** `coderSessionRepo.insert` is called with `{ ..., title: "Plan stage" }`
- **THEN** the inserted row has `title = "Plan stage"`

#### Scenario: Insert without title
- **WHEN** `coderSessionRepo.insert` is called without a title field
- **THEN** the inserted row has `title = NULL`

### Requirement: AcpSessionOptions includes title
`AcpSessionOptions` SHALL include an optional `title?: string` field. When provided, the title SHALL be passed to the coder_session insert in `runAcpSession`.

#### Scenario: runAcpSession with title
- **WHEN** `runAcpSession` is called with `options.title = "Auto-fix: compilation errors"`
- **THEN** the created coder_session row has `title = "Auto-fix: compilation errors"`

#### Scenario: runAcpSession without title
- **WHEN** `runAcpSession` is called without `options.title`
- **THEN** the created coder_session row has `title = NULL`

### Requirement: AcpConnectionOptions includes title
`AcpConnectionOptions` SHALL include an optional `title?: string` field. When provided, the title SHALL be passed to the coder_session insert in `createAcpConnection`.

#### Scenario: createAcpConnection with title
- **WHEN** `createAcpConnection` is called with `options.title = "Plan stage"`
- **THEN** the created coder_session row has `title = "Plan stage"`

#### Scenario: createAcpConnection without title
- **WHEN** `createAcpConnection` is called without `options.title`
- **THEN** the created coder_session row has `title = NULL`

### Requirement: coder_session_started SSE event carries title
The `coder_session_started` SSE event SHALL include the `title` field from the session. When title is NULL, the field SHALL be included as `null`.

#### Scenario: SSE event with title
- **WHEN** a coder session is started with `title: "T-004: Create Plan"`
- **THEN** the `coder_session_started` SSE event payload includes `title: "T-004: Create Plan"`

#### Scenario: SSE event without title
- **WHEN** a coder session is started without a title
- **THEN** the `coder_session_started` SSE event payload includes `title: null`

### Requirement: Each caller provides session-specific title
Each caller of `runAcpSession` or `createAcpConnection` SHALL pass a descriptive `title` string that identifies the session's purpose:

| Caller | Interface | Title Format |
|--------|-----------|-------------|
| RalphExecutor | runAcpSession | `{task.id}: {task.title}` (e.g., `T-004: Create Plan`) |
| PlanStageRunner | createAcpConnection | `"Plan stage"` |
| CheckStageRunner | createAcpConnection | `"Check stage"` |
| CodeCompilesCheck | runAcpSession | `"Auto-fix: compilation errors"` |
| BuildTestCheck | runAcpSession | `"Auto-fix: test failures"` |
| SkillService | runAcpSession | `"Skill: {skill.name}"` |
| ExploreACPService | runAcpSession | `"Explore: {issue.title}"` |

#### Scenario: RalphExecutor passes task title
- **WHEN** RalphExecutor runs task `T-004` with title `"Create Plan and CheckStageRunner"`
- **THEN** it passes `title: "T-004: Create Plan and CheckStageRunner"` to runAcpSession

#### Scenario: PlanStageRunner passes static title
- **WHEN** PlanStageRunner creates an ACP connection
- **THEN** it passes `title: "Plan stage"` to createAcpConnection

#### Scenario: CheckStageRunner passes static title
- **WHEN** CheckStageRunner creates an ACP connection
- **THEN** it passes `title: "Check stage"` to createAcpConnection

#### Scenario: Auto-fix passes descriptive title
- **WHEN** CodeCompilesCheck runs an auto-fix session
- **THEN** it passes `title: "Auto-fix: compilation errors"` to runAcpSession

#### Scenario: SkillService passes skill name
- **WHEN** SkillService runs a skill named `"agent-browser"`
- **THEN** it passes `title: "Skill: agent-browser"` to runAcpSession

#### Scenario: ExploreACPService passes issue title
- **WHEN** ExploreACPService runs for issue titled `"Fix login bug"`
- **THEN** it passes `title: "Explore: Fix login bug"` to runAcpSession
