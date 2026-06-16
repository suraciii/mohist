## ADDED Requirements

### Requirement: mo update displays product-level stages
`mo update` SHALL display user-facing product-level stages during the update process and SHALL NOT remain silent during long waits.

#### Scenario: Update stages are displayed
- **WHEN** user executes `mo update`
- **THEN** the CLI SHALL display a sequence of product-level stages
- **AND** each stage SHALL use user-facing language (e.g., "Updating CLI", "Preparing workflow runner")
- **AND** raw build output or implementation details SHALL NOT be the primary user-facing output

#### Scenario: Long readiness wait shows progress
- **WHEN** `mo update` is waiting for server readiness
- **AND** the wait exceeds a bounded progress interval
- **THEN** the CLI SHALL display the current wait reason (e.g., "waiting for Mohist API", "waiting for Web assets")
- **AND** SHALL update the displayed reason when the readiness stage transitions

#### Scenario: Runner-stopped window is visible
- **WHEN** `mo update` stops the runner for the server update phase
- **THEN** the CLI SHALL display that workflows are paused while the server updates
- **AND** this visibility SHALL persist until the runner is restored

### Requirement: mo update performs recovery on failure or interruption
`mo update` SHALL attempt to restore the runner on failure, timeout, or user interruption when the runner was running before the update began.

#### Scenario: Failed update restores runner
- **WHEN** `mo update` fails after stopping the runner
- **THEN** the CLI SHALL attempt to restart the runner service
- **AND** SHALL report the recovery outcome
- **AND** the exit code SHALL reflect the original failure

#### Scenario: Ctrl-C triggers recovery
- **WHEN** user presses Ctrl-C during `mo update`
- **AND** the runner was stopped for the update
- **THEN** the CLI SHALL attempt best-effort recovery
- **AND** SHALL print the final server and runner availability state

#### Scenario: Recovery failure provides actionable guidance
- **WHEN** runner recovery fails
- **THEN** the CLI SHALL print the specific unavailable capability
- **AND** SHALL provide a direct next action command

### Requirement: mo update reports final outcome by capability
The final output of `mo update` SHALL report one of three outcomes: ready, recovered with warnings, or failed with specific unavailable capabilities.

#### Scenario: Success outcome
- **WHEN** all update stages complete without error
- **THEN** the CLI SHALL print "Update complete. Mohist is ready."
- **AND** exit code SHALL be 0

#### Scenario: Recovered outcome
- **WHEN** update completes with non-critical recovery (e.g., runner restored after failure, skill assets missing)
- **THEN** the CLI SHALL print "Update recovered with warnings"
- **AND** SHALL list the warnings

#### Scenario: Failed outcome with specific capability
- **WHEN** update fails and recovery cannot restore a critical capability
- **THEN** the CLI SHALL print "Update failed: <capability> unavailable"
- **AND** exit code SHALL be non-zero

## MODIFIED Requirements

### Requirement: CLI 支持 server 命令
CLI SHALL 支持 server 管理命令（无需 server 运行）。The `mo update` command SHALL be usable without the server running, and SHALL NOT require server availability to start.

#### Scenario: 启动 server
- **WHEN** 用户执行 `mo server start`
- **THEN** CLI 启动 server 进程
- **AND** CLI 等待 server 就绪
- **AND** CLI 显示 "Server started"

#### Scenario: 停止 server
- **WHEN** 用户执行 `mo server stop`
- **THEN** CLI 发送停止信号给 server
- **AND** CLI 显示 "Server stopped"

#### Scenario: mo update runs without server
- **WHEN** 用户执行 `mo update`
- **AND** server 未运行
- **THEN** CLI SHALL proceed with CLI and managed asset updates
- **AND** SHALL skip server and runner update stages with a clear message
- **AND** SHALL NOT fail with "Server is not running"
