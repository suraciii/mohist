## ADDED Requirements

### Requirement: CoderSessionItem type includes title
The `CoderSessionItem` frontend type SHALL include a `title: string | null` field, matching the backend API response.

#### Scenario: Session with title renders readable label
- **WHEN** a coder session has `title: "T-004: Create Plan and CheckStageRunner"`
- **THEN** the SessionHeader displays `"T-004: Create Plan and CheckStageRunner"` as the session label

#### Scenario: Session without title uses fallback chain
- **WHEN** a coder session has `title: null`
- **THEN** the frontend falls back to: (1) parse taskId from executionId → `"T-004"`, (2) use stage name → `"Plan"`, (3) first 24 chars of taskDescription

### Requirement: Session display uses title with fallback priority
The `getSessionLabel` function (SessionHeader, SessionCard) SHALL determine the session label using the following priority:

1. `session.title` — use directly if non-null and non-empty
2. Parse taskId from `executionId` — e.g., `"build-127-T-004"` → `"T-004"`
3. Stage name — `"Plan"` / `"Check"`
4. `taskDescription` first 24 characters — existing fallback behavior

#### Scenario: Title takes priority over all fallbacks
- **WHEN** a session has `title: "Auto-fix: compilation errors"` and `executionId: "build-5-fix-1"`
- **THEN** the label is `"Auto-fix: compilation errors"`

#### Scenario: Parse taskId from executionId when no title
- **WHEN** a session has `title: null` and `executionId: "build-127-T-004"`
- **THEN** the label is `"T-004"` (parsed from executionId)

#### Scenario: Stage name fallback
- **WHEN** a session has `title: null` and `executionId: "plan-5"` (no task pattern match)
- **THEN** the label is `"Plan"` (derived from executionId prefix)

#### Scenario: TaskDescription last resort
- **WHEN** a session has `title: null` and `executionId` that does not match any known pattern
- **THEN** the label is the first 24 characters of `taskDescription`

### Requirement: SSE coder_session_started event updates live session with title
When the frontend receives a `coder_session_started` SSE event with a `title` field, the corresponding session in `useCoderSessions` SHALL be updated with the title value.

#### Scenario: Live session receives title via SSE
- **WHEN** a `coder_session_started` event arrives with `{ sessionId: "abc", title: "T-004: Create Plan" }`
- **THEN** the session in the useCoderSessions state is updated with `title: "T-004: Create Plan"`

#### Scenario: Live session receives null title via SSE
- **WHEN** a `coder_session_started` event arrives with `{ sessionId: "abc", title: null }`
- **THEN** the session in state has `title: null` and the fallback chain applies for display
