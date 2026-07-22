### Requirement: `mo issue start` returns the WorkflowRun ID

`mo issue start <number>` SHALL create or bind a WorkflowRun for the issue and the CLI output SHALL surface the WorkflowRun ID so the user can immediately pass it to `mo run` commands. In default (human-readable) output, the WorkflowRun ID SHALL be visibly present. With `--json` field selection, the `workflowRunId` field SHALL be selectable.

#### Scenario: Start creates a run and the ID is visible in default output

- **WHEN** the user runs `mo issue start 42` and the start succeeds
- **THEN** the CLI SHALL exit 0
- **AND** the output SHALL contain the WorkflowRun ID in a form the user can copy

#### Scenario: Start with field selection returns the run ID

- **WHEN** the user runs `mo issue start 42 --json workflowRunId`
- **THEN** stdout SHALL contain a JSON object with the `workflowRunId` field

#### Scenario: Start on an already-started issue returns the bound run ID

- **WHEN** the user runs `mo issue start 42` and issue 42 already has an active WorkflowRun `wr_abc123`
- **THEN** the CLI SHALL exit 0
- **AND** the output SHALL contain `wr_abc123`

### Requirement: `mo issue` retains work-item management but not WorkflowRun control

The `mo issue` command tree SHALL retain subcommands for work-item CRUD (`list`, `show`, `create`, `update`), lifecycle (`start`, `done`, `close`, `reopen`, `archive`, `unarchive`, `rebase`), relationships (`prereq add/remove`), comments (`comment add`), templates (`template list/view`), and diff/commit reads (`diff`, `commits`). The `mo issue` command tree SHALL NOT contain WorkflowRun state-changing subcommands: `approve`, `reject`, `retry`, `rerun`, `rerun-from-stage`, `force-stop`, `resume`, or `stop`.

#### Scenario: Issue help lists retained subcommands

- **WHEN** the user runs `mo issue --help`
- **THEN** the help output SHALL list `start`, `done`, `close`, `reopen`, `archive`, `list`, `show`, `create`, `update`
- **AND** SHALL NOT list `approve`, `reject`, `retry`, `rerun`, `rerun-from-stage`, `force-stop`, `resume`, or `stop`

#### Scenario: Removed control verbs fail to resolve

- **WHEN** the user runs any of `mo issue approve 42`, `mo issue reject 42`, `mo issue retry 42`, `mo issue rerun 42`, `mo issue rerun-from-stage 42`, `mo issue force-stop 42`, `mo issue resume 42`, or `mo issue stop 42`
- **THEN** the command SHALL fail to resolve
- **AND** SHALL exit non-zero
- **AND** no HTTP request SHALL be issued

### Requirement: Removed issue control paths are absent from help and error hints

Error messages, hint lines, and help text SHALL NOT reference removed command paths as valid alternatives. When a user attempts a removed path, the error SHALL NOT suggest `mo issue approve`, `mo issue retry`, `mo workflow approve`, or any other removed entry point.

#### Scenario: Error on removed path does not suggest another removed path

- **WHEN** the user runs `mo issue approve 42` and the command fails to resolve
- **THEN** the error or hint output SHALL NOT contain `mo issue approve`, `mo workflow approve`, or `mo issue retry` as a suggestion

### Requirement: Old `mo workflow` execution entry points are removed

The `mo workflow` command tree SHALL NOT contain WorkflowRun execution subcommands: `approve`, `reject`, `retry`, `rerun`, `resume`, `pause`, `stop`, `get`, `show`, `variables`, `events`, or `list-sessions`. These behaviors SHALL be accessible only through `mo run` or through the command group that owns the associated resource in the target surface.

#### Scenario: Removed workflow control verbs fail to resolve

- **WHEN** the user runs any of `mo workflow approve wr_abc`, `mo workflow retry wr_abc`, `mo workflow stop wr_abc`, `mo workflow get wr_abc`, or `mo workflow show wr_abc`
- **THEN** the command SHALL fail to resolve
- **AND** SHALL exit non-zero
- **AND** no HTTP request SHALL be issued

#### Scenario: Workflow help does not list execution subcommands

- **WHEN** the user runs `mo workflow --help`
- **THEN** the output SHALL NOT list `approve`, `reject`, `retry`, `rerun`, `pause`, `resume`, `stop`, `get`, `show`, `variables`, `events`, or `list-sessions`

### Requirement: `mo run` is registered at the root command level

The root command SHALL register a `run` command group so that `mo run --help` resolves and lists the run subcommands.

#### Scenario: Run group appears in root help

- **WHEN** the user runs `mo --help`
- **THEN** the output SHALL list `run` as a command group

#### Scenario: Run help resolves

- **WHEN** the user runs `mo run --help`
- **THEN** the CLI SHALL exit 0
- **AND** the output SHALL list the run subcommands
