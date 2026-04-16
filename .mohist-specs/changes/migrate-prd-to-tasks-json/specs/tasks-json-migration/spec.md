## ADDED Requirements

### Requirement: tasks.json schema definition
The system SHALL define a `TasksFile` TypeScript interface with the following structure:
- `version`: number (required)
- `tasks`: array of `Task` objects (required)

Each `Task` object SHALL have:
- `id`: string (required) — unique task identifier
- `title`: string (required) — human-readable title
- `description`: string (required) — instruction for AI agent
- `order`: number (required) — execution sequence number
- `acceptanceCriteria`: string[] (optional) — verification criteria
- `spec`: string (optional) — spec file reference with optional anchor (e.g., "specs/search/spec.md#REQ-001")
- `dependsOn`: string[] (optional) — documentational dependency declarations
- `passes`: boolean (required, default false) — completion flag
- `attempts`: number (required, default 0) — execution attempt count
- `error`: string | null (optional) — last failure reason

#### Scenario: Valid tasks.json is parsed correctly
- **WHEN** a tasks.json file contains a valid `TasksFile` structure
- **THEN** the system parses it into a `TasksFile` object with all fields accessible

#### Scenario: Minimal task with only required fields
- **WHEN** a task only has id, title, description, order, passes=false, and attempts=0
- **THEN** the system treats optional fields (acceptanceCriteria, spec, dependsOn, error) as undefined/empty/null

### Requirement: task-status.json eliminated
The system SHALL NOT use `task-status.json` for tracking task execution state. All runtime state (passes, attempts, error) SHALL be stored directly on each task in `tasks.json`.

#### Scenario: RalphExecutor updates task state after execution
- **WHEN** RalphExecutor completes a task successfully
- **THEN** it sets `passes: true` on that task in tasks.json and writes the file

#### Scenario: RalphExecutor records failure
- **WHEN** RalphExecutor fails a task
- **THEN** it increments `attempts`, sets `error` to the failure reason on that task in tasks.json

## MODIFIED Requirements

### Requirement: Change directory detection reads tasks.json
The `detectOpenSpecChange` function SHALL look for `tasks.json` instead of `prd.json` as the required artifact. For backward compatibility, if `tasks.json` does not exist but `prd.json` does, the function SHALL fall back to reading `prd.json` and return its path.

#### Scenario: tasks.json exists in change directory
- **WHEN** a change directory contains `tasks.json`
- **THEN** `detectOpenSpecChange` returns `tasksPath` pointing to `tasks.json`

#### Scenario: Legacy prd.json without tasks.json
- **WHEN** a change directory contains `prd.json` but no `tasks.json`
- **THEN** `detectOpenSpecChange` falls back to `prdPath` pointing to `prd.json`

### Requirement: RalphExecutor reads and writes tasks.json directly
The `RalphExecutor` SHALL read task definitions from `tasks.json` (falling back to `prd.json` for legacy changes). It SHALL update `passes`/`attempts`/`error` directly on tasks in the same file. The `readPrdTasks` function SHALL be renamed to `readTasks`. All task-status.json read/write code SHALL be removed.

#### Scenario: Reading tasks from tasks.json
- **WHEN** RalphExecutor reads tasks from a change with `tasks.json`
- **THEN** tasks are parsed with the new field names (acceptanceCriteria, spec, dependsOn, passes, attempts, error)

#### Scenario: Finding next pending task
- **WHEN** RalphExecutor looks for the next task to execute
- **THEN** it finds the first task (by order) where `passes === false`

### Requirement: ContextAssembler uses new Task interface
The `ContextAssembler` SHALL use the new `Task` interface with camelCase field names. The `formatTaskForPrompt` function SHALL reference `acceptanceCriteria` (not `acceptance_criteria`) and `spec` (not `spec_file`).

#### Scenario: Building context for a task
- **WHEN** context is assembled for a task with `spec: "specs/search/spec.md#REQ-001"`
- **THEN** the assembler reads the spec file and includes it in the prompt

### Requirement: read_prd tool renamed to read_tasks
The `read_prd` tool SHALL be renamed to `read_tasks`. It SHALL read `tasks.json` (falling back to `prd.json`) and format the output using new field names including passes/attempts/error status.

#### Scenario: Agent uses read_tasks tool
- **WHEN** an agent calls `read_tasks` with a change path
- **THEN** the tool returns formatted task list with fields: id, title, order, spec, dependsOn, description, acceptanceCriteria, passes, attempts, error

### Requirement: PlannerAgent outputs tasks.json
The `PlannerAgent` SHALL output `tasks.json` instead of `prd.json`. Each task SHALL be generated with `passes: false`, `attempts: 0`, and no `error` field.

#### Scenario: Plan stage completes
- **WHEN** PlannerAgent finishes planning
- **THEN** the change directory contains `tasks.json` (not `prd.json`) with all tasks having `passes: false`
