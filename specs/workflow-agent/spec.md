## ADDED Requirements

### Requirement: Agent tasks execute inside workflow workspace
Agent-backed workflow tasks SHALL execute with `context.workDir` set to the workflow workspace path. Agent execution SHALL NOT use project paths, repository cache paths, repository checkout paths outside the workspace, or user checkout directories as working directories.

#### Scenario: Agent-backed task uses workflow workspace cwd
- **WHEN** an agent-backed task starts for a workflow run
- **THEN** the agent process/session SHALL use the workflow workspace path as its working directory
- **AND** the task prompt context SHALL NOT expose project or repository local execution paths

#### Scenario: Named session reuse preserves workspace boundary
- **WHEN** two agent-session tasks in the same stage attempt reuse the same named session
- **THEN** both prompts SHALL continue to execute inside the same workflow workspace
- **AND** named session reuse SHALL NOT switch the session to a project path, repository cache path, or user checkout path

### Requirement: Non-agent workflow actions preserve cwd isolation
Workflow checks, scripts, OpenSpec sync/archive actions, rebase, repair, merge, and conflict-resolution actions SHALL execute inside `context.workDir` / `workspace.path`. Actions SHALL NOT change cwd to project paths, repository cache paths, external checkouts, or any configured local repository path.

#### Scenario: Merge uses workspace cwd
- **WHEN** `mohist/merge` runs during Integrate
- **THEN** it SHALL perform merge work inside the workflow workspace
- **AND** it SHALL NOT switch to `project.path`, a repository cache path, or a user checkout directory

#### Scenario: Conflict resolver inherits workspace cwd
- **WHEN** merge, rebase, or repair work invokes conflict resolution
- **THEN** the conflict resolver SHALL run with the workflow workspace as cwd
- **AND** conflict files and repair edits SHALL be read and written only inside that workspace

#### Scenario: Checks and scripts use context workDir
- **WHEN** a workflow check or script action executes
- **THEN** its cwd SHALL be `context.workDir`
- **AND** action configuration SHALL NOT opt out of workspace isolation
