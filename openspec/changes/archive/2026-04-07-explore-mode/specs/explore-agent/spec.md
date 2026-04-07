## ADDED Requirements

### Requirement: Explore agent has thinking partner system prompt
The system SHALL provide a dedicated system prompt for the explore agent that positions it as a curious thinking partner, not an executor.

#### Scenario: Agent behavior in explore mode
- **WHEN** explore agent receives a user message
- **THEN** agent responds with curiosity, asks clarifying questions, reads code to verify assumptions, and uses ASCII diagrams when helpful

#### Scenario: Agent proposes crystallization
- **WHEN** agent detects that requirements have converged during conversation
- **THEN** agent MAY suggest creating an issue from the exploration results

### Requirement: Explore agent has read-only tool set
The system SHALL equip the explore agent with only read-only tools (read_file, glob, grep) plus the create_issue bridge tool. The agent SHALL NOT have access to pipeline execution tools (spawn_coder, advance_stage, etc.).

#### Scenario: Agent reads code during exploration
- **WHEN** agent needs to understand the codebase
- **THEN** agent uses read_file, glob, or grep tools to inspect files and search for patterns

#### Scenario: Agent cannot execute code
- **WHEN** explore agent is running
- **THEN** it has no access to spawn_coder, advance_stage, add_comment, or any pipeline execution tools

### Requirement: read_file tool
The system SHALL provide a read_file tool that reads file content from the project directory, with optional line range support.

#### Scenario: Read entire file
- **WHEN** agent calls read_file with a path
- **THEN** system returns the file content within the project directory

#### Scenario: Read file with line range
- **WHEN** agent calls read_file with path, offset, and limit
- **THEN** system returns only the specified line range

#### Scenario: Path traversal prevention
- **WHEN** agent calls read_file with a path outside the project directory
- **THEN** system returns an error and does not read the file

### Requirement: glob tool
The system SHALL provide a glob tool that finds files matching a pattern within the project directory.

#### Scenario: Find files by pattern
- **WHEN** agent calls glob with a pattern like "**/*.ts"
- **THEN** system returns matching file paths relative to the project root

#### Scenario: No matches
- **WHEN** agent calls glob with a pattern that matches no files
- **THEN** system returns an empty array

### Requirement: grep tool
The system SHALL provide a grep tool that searches file contents using regex within the project directory.

#### Scenario: Search for pattern
- **WHEN** agent calls grep with a regex pattern
- **THEN** system returns matching file paths and line numbers with context

#### Scenario: Filter by file extension
- **WHEN** agent calls grep with a pattern and include filter
- **THEN** system only searches files matching the include pattern

### Requirement: create_issue bridge tool
The system SHALL provide a create_issue tool that allows the explore agent to create a draft issue from the exploration results, crystallizing the session.

#### Scenario: Create issue from exploration
- **WHEN** agent calls create_issue with title, body, and optional labels
- **THEN** system creates a draft issue, associates it with the explore session, and returns the issue number

#### Scenario: Structured issue body
- **WHEN** agent calls create_issue
- **THEN** the body SHALL be a structured description derived from the conversation (background, expected behavior, constraints)
