## REMOVED Requirements

### Requirement: CLI 支持本地 Issue CRUD (partial)
**Reason**: The `issue approve`, `issue pause`, and `issue resume` subcommands are dead code in M1. They call removed API endpoints or return errors.
**Migration**: No migration needed. These commands were never functional in M1.

## ADDED Requirements

### Requirement: CLI removes dead workflow commands
The CLI SHALL NOT expose `issue approve`, `issue pause`, or `issue resume` commands. The `issue show` command SHALL NOT display `progress` or `stageInfo` (these fields are removed from the API response). The `formatStage()` function SHALL NOT map `waiting-design-review` or `waiting-review`.

#### Scenario: Dead commands not available
- **WHEN** user executes `mo issue --help`
- **THEN** the output SHALL NOT list `approve`, `pause`, or `resume` subcommands

#### Scenario: Dead command attempted
- **WHEN** user executes `mo issue approve`, `mo issue pause`, or `mo issue resume`
- **THEN** CLI SHALL display an unknown command error

#### Scenario: Issue show omits progress display
- **WHEN** user executes `mo issue show <number>`
- **THEN** the output SHALL NOT display progress bar or stage info block

### Requirement: Server status CLI omits task/worker info
The `mo server status` command SHALL NOT display `Workers`, `Running tasks`, or `Queued tasks` lines. The `fetchServerStatus()` function SHALL NOT reference removed status fields.

#### Scenario: Server status display
- **WHEN** user executes `mo server status` and server is running
- **THEN** the output SHALL NOT include lines about workers, running tasks, or queued tasks
