## MODIFIED Requirements

### Requirement: Sub-agent spawning (opencode subprocess)

The system SHALL support spawning opencode as a subprocess from the Main Agent via the spawn_agent tool. M1 implementation: spawn_agent directly spawns `opencode agent --local --message <task>` in the issue's worktree, synchronously waits for completion, and returns stdout/stderr/exit_code. No child LLM loop in M1.

#### Scenario: Spawn and wait
- **WHEN** the Main Agent calls spawn_agent with agent_type, task, and cwd
- **THEN** the system SHALL spawn an opencode subprocess in the cwd
- **THEN** the system SHALL wait for the subprocess to complete
- **THEN** the subprocess output (stdout/stderr/exit_code) SHALL be returned as a tool result

#### Scenario: Sub-agent timeout
- **WHEN** the opencode subprocess exceeds the configured timeout (default 30 minutes)
- **THEN** the system SHALL kill the subprocess
- **THEN** a timeout error SHALL be returned to the Main Agent

#### Scenario: Sub-agent failure
- **WHEN** the opencode subprocess exits with non-zero code
- **THEN** the stderr output and exit code SHALL be returned to the Main Agent
- **THEN** the Main Agent LLM SHALL decide how to handle the failure

### Requirement: AcpSessionOptions accepts model override

`AcpSessionOptions` and `AcpConnectionOptions` SHALL accept an optional `model?: string` field. When provided, this value SHALL be passed as a session config option to the ACP session, overriding any stage-level or global model configuration.

#### Scenario: ACP session with per-issue model
- **WHEN** `runAcpSession` is called with `options.model = "openai/gpt-4o"`
- **THEN** the ACP session SHALL use `"openai/gpt-4o"` as the coder model
- **AND** stageModels and global model are ignored

#### Scenario: ACP session without per-issue model
- **WHEN** `runAcpSession` is called without `options.model` (undefined)
- **THEN** the ACP session SHALL fall through to stageModels/global/default model resolution

#### Scenario: ACP connection with per-issue model
- **WHEN** `createAcpConnection` is called with `options.model = "openai/gpt-4o"`
- **THEN** the ACP connection SHALL use `"openai/gpt-4o"` for all prompts in that connection
