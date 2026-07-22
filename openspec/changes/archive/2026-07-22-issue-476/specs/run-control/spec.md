### Requirement: State-changing verbs exist exclusively under `mo run`

The verbs `approve`, `reject`, `retry`, `rerun`, `pause`, `resume`, and `stop` SHALL be subcommands of `mo run` and SHALL NOT appear as subcommands of `mo issue`, `mo workflow`, or any other command group. The command tree, group help, leaf help, and error hints SHALL NOT reference the removed paths (`mo issue approve`, `mo workflow approve`, `mo issue retry`, etc.) as valid alternatives.

#### Scenario: Run help lists the seven control verbs

- **WHEN** the user runs `mo run --help`
- **THEN** the help output SHALL list `approve`, `reject`, `retry`, `rerun`, `pause`, `resume`, and `stop` as subcommands
- **AND** the output SHALL NOT list a `rerun-from-stage` subcommand

#### Scenario: Removed issue control verbs do not resolve

- **WHEN** the user runs `mo issue approve 42`
- **THEN** the command SHALL fail to resolve and SHALL exit non-zero
- **AND** no HTTP request SHALL be issued

#### Scenario: Removed workflow control verbs do not resolve

- **WHEN** the user runs `mo workflow approve wr_abc`
- **THEN** the command SHALL fail to resolve and SHALL exit non-zero
- **AND** no HTTP request SHALL be issued

### Requirement: Each control verb targets exactly one WorkflowRun

Every control verb SHALL accept exactly one target: either a positional WorkflowRun ID argument or the `--issue <number>` option. Providing both SHALL fail locally with exit code 2 and SHALL NOT issue an HTTP request. Providing neither SHALL fail locally with exit code 2 and SHALL NOT issue an HTTP request.

#### Scenario: Run ID positional argument targets the run directly

- **WHEN** the user runs `mo run approve wr_abc123`
- **THEN** the CLI SHALL send the action to the workflow-run-scoped endpoint for `wr_abc123`
- **AND** SHALL NOT resolve a project or issue number

#### Scenario: Issue selector resolves the bound WorkflowRun

- **WHEN** the user runs `mo run approve --issue 42` and issue 42 is bound to WorkflowRun `wr_abc123`
- **THEN** the CLI SHALL resolve `wr_abc123` from the issue's bound run
- **AND** SHALL send the action to the workflow-run-scoped endpoint for `wr_abc123`

#### Scenario: Both target and selector provided fails locally

- **WHEN** the user runs `mo run approve wr_abc123 --issue 42`
- **THEN** the CLI SHALL exit with code 2
- **AND** SHALL print a usage error explaining that only one target may be provided
- **AND** no HTTP request SHALL be issued

#### Scenario: Neither target nor selector provided fails locally

- **WHEN** the user runs `mo run approve`
- **THEN** the CLI SHALL exit with code 2
- **AND** SHALL print a usage error explaining that a Run ID or `--issue` is required
- **AND** no HTTP request SHALL be issued

### Requirement: `--issue` resolution fails clearly when no run is bound

When `--issue <number>` is used and the issue has no bound WorkflowRun (the issue has not been started, or its run has been removed), the CLI SHALL fail with a non-zero exit and a diagnostic message that names the issue and states it has no active run. The diagnostic SHALL NOT suggest a removed command path.

#### Scenario: Issue without a run reports the missing binding

- **WHEN** the user runs `mo run approve --issue 99` and issue 99 has no `workflowRunId`
- **THEN** the CLI SHALL exit non-zero
- **AND** the error output SHALL name issue 99 and state it has no active workflow run
- **AND** the error output SHALL NOT reference `mo issue approve` or `mo workflow approve`

### Requirement: `approve` passes the approval gate

`mo run approve` SHALL POST to the workflow-run approve endpoint with an empty body. A successful response SHALL exit 0. A server-side failure (run not active, not at a gate, run not found) SHALL be surfaced on stderr with the server's message and error code, and SHALL exit non-zero.

#### Scenario: Approve a run at its gate

- **WHEN** the user runs `mo run approve wr_abc123` and the run is awaiting approval
- **THEN** the CLI SHALL POST to `/api/workflow-runs/wr_abc123/approve` with an empty body
- **AND** SHALL exit 0 on success

#### Scenario: Approve a run that is not active

- **WHEN** the server responds with a conflict indicating the run is not active
- **THEN** the CLI SHALL exit non-zero
- **AND** stderr SHALL contain the server's error message and error code

### Requirement: `reject` requires a reason message

`mo run reject` SHALL require a non-empty `--message` (or `-m`) option. A missing or whitespace-only message SHALL fail locally with exit code 1 and SHALL NOT issue an HTTP request. When provided, the message SHALL be forwarded in the request body as `message`.

#### Scenario: Reject without message fails locally

- **WHEN** the user runs `mo run reject wr_abc123`
- **THEN** the CLI SHALL exit 1
- **AND** stderr SHALL mention `--message`
- **AND** no HTTP request SHALL be issued

#### Scenario: Reject with message forwards the reason

- **WHEN** the user runs `mo run reject wr_abc123 --message "Rework the auth flow"`
- **THEN** the CLI SHALL POST to `/api/workflow-runs/wr_abc123/reject` with body `{"message":"Rework the auth flow"}`

### Requirement: `retry` retries the current failure point

`mo run retry` SHALL POST to the workflow-run retry endpoint with an empty body. This retries the current failed task or check and restores the manual-retry recovery budget. It SHALL NOT accept a stage selector — retrying a different stage is the domain of `rerun --from-stage`.

#### Scenario: Retry a failed run

- **WHEN** the user runs `mo run retry wr_abc123`
- **THEN** the CLI SHALL POST to `/api/workflow-runs/wr_abc123/retry` with an empty body

### Requirement: `rerun` restarts execution and supports `--from-stage`

`mo run rerun` without `--from-stage` SHALL POST to the rerun endpoint with an empty body, causing the entire Run to execute from the beginning. With `--from-stage <stage>` it SHALL POST to the rerun-from-stage endpoint with body `{"stage":"<stage>"}`, invalidating the target stage and all later stages. A blank `--from-stage` value SHALL fail locally with exit code 1 and SHALL NOT issue an HTTP request. The `rerun-from-stage` action SHALL NOT exist as a separate subcommand.

#### Scenario: Rerun from the start

- **WHEN** the user runs `mo run rerun wr_abc123`
- **THEN** the CLI SHALL POST to `/api/workflow-runs/wr_abc123/rerun` with an empty body

#### Scenario: Rerun from a specific stage

- **WHEN** the user runs `mo run rerun wr_abc123 --from-stage build`
- **THEN** the CLI SHALL POST to `/api/workflow-runs/wr_abc123/rerun-from-stage` with body `{"stage":"build"}`

#### Scenario: Blank from-stage fails locally

- **WHEN** the user runs `mo run rerun wr_abc123 --from-stage "   "`
- **THEN** the CLI SHALL exit 1
- **AND** stderr SHALL mention `--from-stage`
- **AND** no HTTP request SHALL be issued

### Requirement: `pause` is resumable and `stop` is terminal

`mo run pause` SHALL POST to the pause endpoint, placing the Run in a paused state that can later be resumed via `mo run resume`. `mo run stop` SHALL POST to the stop endpoint, permanently terminating the Run so that it can never be resumed. The stop leaf help SHALL state that the action is terminal and SHALL direct the user to `pause` if a resumable interruption is desired.

#### Scenario: Pause places the run in a paused state

- **WHEN** the user runs `mo run pause wr_abc123`
- **THEN** the CLI SHALL POST to `/api/workflow-runs/wr_abc123/pause`

#### Scenario: Stop terminates the run permanently

- **WHEN** the user runs `mo run stop wr_abc123`
- **THEN** the CLI SHALL POST to `/api/workflow-runs/wr_abc123/stop`

#### Scenario: Stop help explains terminality and points to pause

- **WHEN** the user runs `mo run stop --help`
- **THEN** the help output SHALL state that stop is terminal or permanent
- **AND** SHALL mention `pause` as the resumable alternative

#### Scenario: Resume after stop is rejected by the server

- **WHEN** the user runs `mo run resume wr_abc123` after the run has been stopped
- **THEN** the server SHALL reject the request
- **AND** the CLI SHALL surface the server's error message and exit non-zero

### Requirement: `stop` requires explicit confirmation in non-interactive contexts

Because `stop` is irreversible, the CLI SHALL require explicit confirmation before executing it. In an interactive terminal, the CLI MAY prompt the user for confirmation. In a non-interactive context (piped stdin or `MOHIST_PROMPT_DISABLED=1`), the CLI SHALL require the `--yes` flag; without `--yes` it SHALL fail with exit code 1 and SHALL NOT issue an HTTP request. The `--yes` flag SHALL bypass any interactive prompt and proceed directly.

#### Scenario: Stop without --yes in non-interactive mode is rejected

- **WHEN** the CLI is running with non-interactive input and the user runs `mo run stop wr_abc123`
- **THEN** the CLI SHALL exit 1
- **AND** stderr SHALL state that `--yes` is required for this irreversible action
- **AND** no HTTP request SHALL be issued

#### Scenario: Stop with --yes in non-interactive mode proceeds

- **WHEN** the CLI is running with non-interactive input and the user runs `mo run stop wr_abc123 --yes`
- **THEN** the CLI SHALL POST to `/api/workflow-runs/wr_abc123/stop`

#### Scenario: Other control verbs do not require --yes

- **WHEN** the CLI is running with non-interactive input and the user runs `mo run pause wr_abc123`
- **THEN** the CLI SHALL proceed without requiring `--yes`
- **AND** SHALL POST to the pause endpoint

### Requirement: `resume` continues a paused run

`mo run resume` SHALL POST to the resume endpoint with an empty body, continuing a paused Run from where it was interrupted.

#### Scenario: Resume a paused run

- **WHEN** the user runs `mo run resume wr_abc123`
- **THEN** the CLI SHALL POST to `/api/workflow-runs/wr_abc123/resume` with an empty body

### Requirement: Server errors are surfaced with message and code

When the server returns a failure for any control verb, the CLI SHALL print the server's error message and stable error code on stderr and SHALL exit non-zero. The CLI SHALL NOT replace the server's specific reason with a generic message.

#### Scenario: Structured server error is surfaced verbatim

- **WHEN** the server responds to a rerun-from-stage request with code `stage_not_reached` and message "Stage 'integrate' has not been reached"
- **THEN** stderr SHALL contain `stage_not_reached`
- **AND** stderr SHALL contain "Stage 'integrate' has not been reached"
- **AND** the CLI SHALL exit non-zero

#### Scenario: Run-not-found error is surfaced

- **WHEN** the server responds with a not-found error for an unknown Run ID
- **THEN** stderr SHALL contain the server's not-found message
- **AND** the CLI SHALL exit non-zero

### Requirement: `--json` field selection works on mutation responses

Control verbs that return a resource on success SHALL support `--json <fields>` field selection, projecting only the requested fields as a JSON object. Bare `--json` SHALL list the available fields and exit 0 without issuing the mutation. The output SHALL NOT include an `{ok,data,error}` wrapper.

#### Scenario: Approve with field selection projects the result

- **WHEN** the user runs `mo run approve wr_abc123 --json workflowRunId,approved`
- **THEN** stdout SHALL contain a JSON object with `workflowRunId` and `approved`
- **AND** stdout SHALL NOT contain `success` or `data` wrapper keys
