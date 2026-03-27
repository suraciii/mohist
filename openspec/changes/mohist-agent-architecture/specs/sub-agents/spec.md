## ADDED Requirements

### Requirement: Sub-agent types
The system SHALL define four sub-agent types: Explore, Plan, Code, and Verify. Each sub-agent type SHALL have its own prompt, tool set, and optional model override defined in code (not in workflow.yaml).

#### Scenario: Explore agent spawned
- **WHEN** the Main Agent spawns an Explore sub-agent
- **THEN** the sub-agent SHALL use the Explore prompt and tool set (read, glob, grep, ask_user, add_comment)

#### Scenario: Plan agent spawned
- **WHEN** the Main Agent spawns a Plan sub-agent
- **THEN** the sub-agent SHALL use the Plan prompt and tool set (read, glob, grep, write)

#### Scenario: Code agent spawned
- **WHEN** the Main Agent spawns a Code sub-agent
- **THEN** the sub-agent SHALL use the Code prompt and tool set (read, glob, grep, call_opencode)

#### Scenario: Verify agent spawned
- **WHEN** the Main Agent spawns a Verify sub-agent
- **THEN** the sub-agent SHALL use the Verify prompt and tool set (read, glob, grep, bash)

### Requirement: Explore agent
The Explore agent SHALL analyze the issue requirements by reading the codebase context, asking the user clarifying questions, and producing a clear requirements document with acceptance criteria. The Explore agent SHALL use the ask_user tool to interact with the user.

#### Scenario: Explore with clarifying questions
- **WHEN** the Explore agent identifies ambiguities in the issue
- **THEN** it SHALL use ask_user to ask clarifying questions
- **THEN** it SHALL record the clarified requirements

#### Scenario: Explore completion
- **WHEN** the Explore agent has gathered sufficient information
- **THEN** it SHALL produce a clear requirements description and acceptance criteria
- **THEN** it SHALL return the results to the Main Agent

### Requirement: Plan agent
The Plan agent SHALL analyze the codebase and produce a technical plan with task breakdown. The Plan agent SHALL read existing code, identify affected files, and write the plan to a file in the worktree.

#### Scenario: Plan generation
- **WHEN** the Plan agent is spawned with the explored requirements
- **THEN** it SHALL analyze the codebase structure
- **THEN** it SHALL produce a technical plan with specific, executable tasks
- **THEN** it SHALL write the plan to a file in the worktree

### Requirement: Code agent
The Code agent SHALL implement code changes by calling opencode as a subprocess. The Code agent SHALL NOT directly read/write/edit code itself. It SHALL delegate all coding work to opencode via the call_opencode tool.

#### Scenario: Code execution via opencode
- **WHEN** the Code agent needs to implement a task
- **THEN** it SHALL call the call_opencode tool with a detailed prompt
- **THEN** call_opencode SHALL spawn an opencode subprocess in the issue's worktree
- **THEN** the subprocess SHALL execute until completion or timeout
- **THEN** the output SHALL be returned to the Code agent

#### Scenario: Code agent does not directly edit code
- **WHEN** the Code agent is running
- **THEN** it SHALL NOT have write, edit, or bash tools (except via call_opencode)

### Requirement: Verify agent
The Verify agent SHALL verify code quality by running tests, reviewing code changes, and checking for issues. The Verify agent SHALL use bash to run tests and read tools to review code.

#### Scenario: Verify with tests
- **WHEN** the Verify agent is spawned
- **THEN** it SHALL run the project's test suite
- **THEN** it SHALL review code changes (diff)
- **THEN** it SHALL report any issues found

#### Scenario: Verify auto-fix
- **WHEN** the Verify agent finds fixable issues
- **THEN** it MAY attempt to fix them (via bash tools for running fix commands)
- **THEN** it SHALL re-verify after fixing

### Requirement: Sub-agent tool isolation
Each sub-agent SHALL only have access to its designated tool set. Sub-agents SHALL NOT be able to spawn further sub-agents. The tool set SHALL be defined in the agent's code definition and enforced at runtime.

#### Scenario: Explore agent tool restriction
- **WHEN** the Explore agent attempts to use a tool not in its tool set (e.g., write, edit)
- **THEN** the tool call SHALL be rejected

#### Scenario: No recursive spawning
- **WHEN** any sub-agent attempts to call spawn_agent
- **THEN** the tool call SHALL be rejected
