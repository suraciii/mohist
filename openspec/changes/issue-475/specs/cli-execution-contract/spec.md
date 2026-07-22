### Requirement: Exit codes classify every command outcome

The CLI SHALL use exit code `0` for success, `1` for a domain, service, or operational failure, `2` for unknown command areas or actions and all local usage errors, and `130` for user cancellation. A command MUST NOT report success when its requested operation has failed or its completion is unknown.

#### Scenario: A local usage error occurs

- **WHEN** an operator supplies an unknown command area, unknown action, invalid option, or invalid argument
- **THEN** the CLI SHALL exit with code `2`

#### Scenario: A service or domain operation fails

- **WHEN** a valid command reaches a Mohist service and the operation is rejected or cannot be completed
- **THEN** the CLI SHALL exit with code `1`

#### Scenario: The user cancels a command

- **WHEN** an operator interrupts an active command
- **THEN** the CLI SHALL stop the operation and exit with code `130`

### Requirement: Local usage diagnostics are scoped and offline

For an unknown area, unknown action, or invalid local argument, the CLI SHALL write the error and only the nearest command-level usage to stderr. `--help`, JSON field discovery, and local usage failures SHALL NOT require a Server, Project, or Runner.

#### Scenario: An action is unknown within a valid area

- **WHEN** an operator invokes an unknown action beneath a valid command area
- **THEN** stderr SHALL contain the error and usage for that area only
- **AND** the CLI SHALL exit with code `2` without contacting a Mohist service

### Requirement: Command results and diagnostics use separate streams

Every command SHALL write its normal result to stdout and SHALL write errors, recovery hints, confirmations, and progress information to stderr. A diagnostic MUST NOT be mixed into a command's successful result stream.

#### Scenario: A command reports progress before completing

- **WHEN** a command emits progress and then completes successfully
- **THEN** stdout SHALL contain only the command result
- **AND** the progress information SHALL be written to stderr

### Requirement: Domain failures retain actionable context

Every domain or service failure diagnostic SHALL include a stable error code. When the failure response provides an affected object, current state, or rejection reason, the diagnostic SHALL preserve those values. The CLI SHALL emit exactly one executable recovery hint only when the applicable recovery action is unambiguous; it MUST NOT emit a hint when it cannot determine one.

#### Scenario: A state transition is rejected with context

- **WHEN** a domain command is rejected with an error code, affected object, current state, and rejection reason
- **THEN** stderr SHALL preserve the code, object, state, and reason
- **AND** SHALL include one executable recovery hint only when the rejection identifies one unambiguous next action

#### Scenario: A failure has no deterministic recovery action

- **WHEN** a domain or service failure has no unambiguous recovery action
- **THEN** stderr SHALL include the stable error code and available failure context
- **AND** SHALL NOT include a recovery hint

### Requirement: Non-interactive invocations never wait for input

When stdin is not a TTY or `MOHIST_PROMPT_DISABLED=1` is set, the CLI SHALL NOT prompt or wait for input. A command that needs a value or confirmation in that mode SHALL fail immediately with a stderr diagnostic that identifies the missing requirement and an explicit non-interactive way to provide it.

#### Scenario: A confirmation is required in a non-TTY invocation

- **WHEN** a command that would require confirmation is invoked with stdin redirected
- **THEN** the command SHALL NOT read from stdin or modify state
- **AND** SHALL write a diagnostic to stderr identifying the required explicit input
- **AND** SHALL exit with code `1`

#### Scenario: Prompts are disabled by environment

- **WHEN** `MOHIST_PROMPT_DISABLED=1` is set and a command would prompt
- **THEN** the command SHALL fail immediately without prompting or modifying state
- **AND** SHALL exit with code `1`

### Requirement: Mutating transport failures are not silently replayed

For a state-changing request interrupted by a transport failure, the CLI SHALL distinguish a failure known to occur before submission from one whose server-side result is unknown. The CLI MAY report that a known-unsubmitted request was not sent, but it MUST NOT automatically retry a request with an unknown result and MUST NOT report that operation as successful.

#### Scenario: A request cannot be submitted

- **WHEN** a state-changing request fails before it is sent to the Server
- **THEN** stderr SHALL state that the request was not submitted
- **AND** the CLI SHALL exit with code `1`

#### Scenario: A submitted request loses its response

- **WHEN** a state-changing request is sent but the CLI loses the response before learning the result
- **THEN** stderr SHALL state that the operation result is unknown
- **AND** the CLI SHALL exit with code `1`
- **AND** SHALL NOT automatically retry the request
