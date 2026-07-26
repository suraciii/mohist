### Requirement: Local and side-effect-free help
Every `--help` invocation SHALL complete successfully using only the local command model. Help MUST NOT resolve a Project, contact the Server, prompt for input, or perform another side effect.

#### Scenario: Requesting leaf help without a Server
- **WHEN** a user runs `mo run retry --help` while the Server is unavailable
- **THEN** the command SHALL exit successfully and describe the retry invocation locally
- **AND THEN** no Server request or Project resolution MUST occur

### Requirement: Root help as a capability index
Root help SHALL present one product description, usage, command groups named Work, Automation, Operations, and Tools, a one-sentence result description for each listed command, two or three discovery/read/recovery examples, and the `mo help <topic>` and documentation entry points. Root help MUST NOT enumerate leaf flags or expand subcommand trees.

#### Scenario: Discovering a Run control action
- **WHEN** a user runs `mo --help`
- **THEN** the output SHALL identify `run` in the Automation group and describe its purpose
- **AND THEN** the output MUST direct the user to the Run command group rather than listing `run retry` flags

#### Scenario: Following the shared-help entry
- **WHEN** root help directs a user to `mo help <topic>`
- **THEN** every advertised topic MUST resolve locally and print its shared rule

### Requirement: Shared help topics
`mo help <topic>` SHALL provide the shared topics `output`, `environment`, and `exit-codes`. Each topic MUST describe only cross-command rules that cannot be learned from a leaf command, MUST execute without Project resolution or Server access, and MUST return a scoped usage error for an unknown topic.

#### Scenario: Reading output rules
- **WHEN** a user runs `mo help output`
- **THEN** stdout SHALL describe the shared human output, JSON field-selection, and stdout/stderr rules
- **AND THEN** the command SHALL exit successfully without performing a remote request

#### Scenario: Requesting an unknown topic
- **WHEN** a user runs `mo help unknown`
- **THEN** the CLI SHALL exit with code 2 and print the `mo help` usage to stderr
- **AND THEN** the command MUST NOT resolve a Project or contact the Server

### Requirement: Command-group help establishes boundaries
Each command-group help output SHALL state the resource or task boundary and scope, show usage, and list its actions with one-sentence result descriptions. It MUST include `SEE ALSO` only when an adjacent command group would otherwise be confused with the current group.

#### Scenario: Distinguishing Workflow Profiles from WorkflowRuns
- **WHEN** a user runs `mo workflow --help`
- **THEN** the output SHALL state that `workflow` manages Project-scoped Workflow Profiles
- **AND THEN** the output SHALL direct users who need execution control to `mo run --help`

#### Scenario: Choosing a Run target
- **WHEN** a user runs `mo run --help`
- **THEN** the output SHALL state that it manages WorkflowRuns and that a Run can be addressed by Run ID or `--issue`

### Requirement: Leaf help enables an exact invocation
Leaf help SHALL state its resulting behavior, one or more legal usage forms, arguments and options including requiredness, defaults, mutual exclusion, and allowed values. It MUST describe state prerequisites, irreversible effects, or nearby action distinctions only where those facts change the user's choice. Resource-result leaf help SHALL list its supported JSON fields and MAY contain at most three independent executable examples.

#### Scenario: Explaining irreversible Run termination
- **WHEN** a user runs `mo run stop --help`
- **THEN** the output SHALL state that stop is terminal and identify the explicit confirmation requirement
- **AND THEN** the output SHALL distinguish the resumable pause action when that distinction affects the choice

#### Scenario: Discovering JSON fields for an Issue view
- **WHEN** a user runs `mo issue view --help`
- **THEN** the output SHALL list the JSON fields accepted by that command's `--json` selection
- **AND THEN** those fields MUST match the fields that the command accepts at runtime

### Requirement: Help excludes non-user-facing detail
Help text SHALL describe current product behavior only. It MUST NOT contain API routes, HTTP methods, internal type or source names, implementation paths, historical issue identifiers, migration aliases, or general shell and Agent tutorials.

#### Scenario: Reading migrated command help
- **WHEN** a user requests help for a canonical command replacing an old path
- **THEN** the help SHALL describe only the canonical command's behavior
- **AND THEN** the help MUST NOT mention the removed command or describe it as an equivalent path

### Requirement: Usage errors are scoped and actionable
An unknown area, unknown action, invalid local argument, mutually exclusive input, or invalid JSON field SHALL exit with code 2, write a specific diagnostic and the nearest relevant usage to stderr, and perform no remote request. A known operation failure SHALL exit nonzero with a stable product error code; it SHALL include exactly one executable `hint:` only when recovery is certain.

#### Scenario: Invoking a removed command alias
- **WHEN** a user invokes a removed action such as `mo issue show 42`
- **THEN** the CLI SHALL exit with code 2 and print the nearest Issue usage to stderr
- **AND THEN** it MUST NOT print root help successfully or issue a remote request

#### Scenario: Handling an unrecoverable domain rejection
- **WHEN** a Server rejects a known command because its current state is invalid and no certain recovery exists
- **THEN** stderr SHALL retain the object, rejection reason, and stable error code
- **AND THEN** the CLI MUST NOT append a speculative `hint:`
