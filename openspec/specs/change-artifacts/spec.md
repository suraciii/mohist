# OpenSpec Capability: change-artifacts

### Requirement: Change directory structure

The system SHALL create a standardized directory structure for each Change under `openspec/changes/{issue-number}-{slug}/`.

**Naming Convention:**
- Base name: `{issue.number}-{slug}`
- Slug: kebab-case version of issue title, max 50 characters
- Conflict resolution: If directory exists, auto-append `-v2`, `-v3`, etc.

#### Scenario: Create new change
- **WHEN** the system initiates a new Change for an issue
- **THEN** it creates the directory `openspec/changes/{issue-number}-{slug}/`
- **AND** it creates the following files:
  - `.change.json` with metadata (name, issue_id, status, order, created_at)
  - `proposal.md` for the change motivation
  - `design.md` for technical approach
  - `specs/` directory for detailed requirements
  - `tasks.json` (generated during plan phase after self-review)
  - `session-memories/` directory for task learnings

#### Scenario: Handle naming conflict
- **WHEN** creating a Change for issue #42 "Add user authentication"
- **AND** `42-add-user-authentication/` already exists
- **THEN** the system creates `42-add-user-authentication-v2/`

### Requirement: Change metadata file

The system SHALL maintain a `.change.json` file with essential Change metadata.

#### Scenario: Read change metadata
- **WHEN** the system needs to access Change information
- **THEN** it reads `openspec/changes/{name}/.change.json`
- **AND** the file contains: name, issue_id, status, created_at

#### Scenario: Update change status
- **WHEN** the Change transitions between stages (planning, reviewing, building, verifying, done)
- **THEN** the system updates the `status` field in `.change.json`
- **AND** the change is persisted to disk

### Requirement: Change archival

The system SHALL support archiving completed Changes.

#### Scenario: Archive completed change
- **WHEN** a Change reaches `done` status
- **THEN** the system moves the directory from `openspec/changes/{name}/` to `openspec/changes/archive/YYYY-MM-DD-{name}/`
- **AND** the session-memories are preserved in the archive
- **AND** the Change remains accessible for historical reference

### Requirement: Issue-Change relationship tracking

The system SHALL track the relationship between Issues and Changes.

#### Scenario: Track multiple changes for one issue
- **WHEN** an issue has multiple Changes over time
- **THEN** the system maintains a list in the issue metadata
- **AND** the list contains all Changes with their status and order
- **AND** users can view the Change history for an issue

### Requirement: REQ-CA-001 Durable workflow artifacts are preserved files only

Workflow artifacts SHALL refer only to durable files intended to be preserved with the OpenSpec change or archived workflow context. Build logs, test output, command stdout/stderr, transient error summaries, agent session streams, health gate results, and parsed review verdicts SHALL NOT be reported as durable artifacts.

#### Scenario: Durable artifact paths are reported
- **WHEN** plan or check tasks create `proposal.md`, `specs/`, `design.md`, `tasks.json`, `self-review.md`, `review.md`, or `review-self-check.md`
- **THEN** task results MAY list those paths in `artifacts`

#### Scenario: Transient evidence is not an artifact
- **WHEN** a command, health gate, test run, AI review parse, or agent session produces logs or evidence
- **THEN** that data SHALL be stored in `CheckResult.output`, `StageTaskResult.output`, or execution/session logs
- **AND** it SHALL NOT be listed in `artifacts`

### Requirement: REQ-CA-002 Task execution result supports transient output

Stage task results SHALL support optional transient execution output separately from durable artifact paths. Existing persisted task results without `output` SHALL remain readable.

#### Scenario: Task output records transient details
- **WHEN** a task records command excerpts, error summaries, agent session status, changed-file summaries, or fix evidence
- **THEN** that information SHALL be stored in task `output`
- **AND** older task result records without `output` SHALL still deserialize successfully

