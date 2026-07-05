### Requirement: The `mo workflow` group offers direct workflowRunId entry points for the read views of a run

The `mo workflow` command group SHALL provide read commands that address a WorkflowRun directly by its `workflowRunId`: `show`, `status`, `variables`, `events`, and `list-sessions`. Each read command SHALL identify its target solely from the run id and SHALL NOT require an issue number or project ref. The read surface is governed by a strict three-way distinction between output format, subresource, and associated resource — the three MUST NOT be mixed.

#### Scenario: Every read verb is present on the workflow group

- **WHEN** the `mo workflow` group is introspected (e.g. `mo workflow --help`)
- **THEN** the group SHALL expose `show`, `status`, `variables`, `events`, and `list-sessions` subcommands
- **AND** SHALL NOT expose a separate `yaml` subcommand (template-definition YAML rides on `show -o yaml`)

#### Scenario: A read command addresses a run by id without a project or issue number

- **WHEN** a caller runs `mo workflow show <runId>` (or any other read verb) with only the workflowRunId
- **THEN** the CLI SHALL resolve the target run solely from that id
- **AND** SHALL NOT require `--project` / `--project-id` or an issue number to identify the run

### Requirement: `show` returns the full run resource and its YAML rendering carries the template definition

`mo workflow show <runId>` SHALL return the full WorkflowRun resource — run identity, current status, stage progress, approval state, and workflow-definition metadata — governed by the shared `-o` output-format flag (`table` / `json` / `yaml`). With `-o yaml`, `show` SHALL render the workflow template-definition YAML for the run. There SHALL NOT be a dedicated `mo workflow yaml` command: the template-definition YAML is an output format of `show`, not a separate resource command. This is the canonical example of the "output format never creates a command" rule.

#### Scenario: show renders the full resource in the requested format

- **WHEN** a caller runs `mo workflow show <runId> -o <table|json|yaml>`
- **THEN** the command SHALL render the full run resource in that format
- **AND** SHALL use the same shared output-format path as other `mo` read commands

#### Scenario: show -o yaml carries the template definition

- **WHEN** a caller runs `mo workflow show <runId> -o yaml`
- **THEN** the rendered YAML SHALL include the workflow template definition for the run
- **AND** the CLI SHALL NOT require a separate `mo workflow yaml` command to obtain it

### Requirement: The `show` read model carries associated-issue context

The `show` read model SHALL include the issue associated with the run — at minimum the issue number and title — so that a consumer holding only a `workflowRunId` (an agent event-subscription handler, a script) can correlate the run to its issue without any reverse-resolution call. This is the hard prerequisite declared by `design/agent-subscriptions.md` ("Agent uses `mo workflow get <runId>` to pull context including the associated issue"); `show` is the command that satisfies it, and the capability closes that stated prerequisite.

#### Scenario: An agent handler correlates a run to its issue from show alone

- **WHEN** a consumer that knows only the `workflowRunId` runs `mo workflow show <runId> -o json`
- **THEN** the response SHALL contain the associated issue's number and title
- **AND** the consumer SHALL NOT need to perform any additional lookup to learn which issue the run belongs to

### Requirement: `status` returns a compact summary shorter than `show`

`mo workflow status <runId>` SHALL return a compact status summary of the run — shorter than the full `show` resource (current stage, run status, stage progress at a glance). Because the workflow state machine is more complex than a single-phase workload, a dedicated compact view (rather than only `show`) SHALL remain available, and SHALL honor `-o` for format selection.

#### Scenario: status is more compact than show

- **WHEN** a caller runs `mo workflow status <runId>` and separately `mo workflow show <runId>` for the same run
- **THEN** the `status` output SHALL be a compact summary focused on current state and progress
- **AND** the `show` output SHALL be the full resource (a strict superset of the status view)

### Requirement: `variables` addresses the effective-variables subresource with independent keyPath addressing

`mo workflow variables <runId>` SHALL address the effective-variables subresource of the run — a true subresource with its own addressing, not an output format of `show`. It SHALL support `--stage <stage>` to scope the effective-variable resolution to a stage, and `--key <keyPath>` to address a single variable by dotted key path. Effective variables MUST NOT be reachable only via `-o` on `show`; the subresource has its own command because it has its own resource path.

#### Scenario: variables lists effective variables for the run

- **WHEN** a caller runs `mo workflow variables <runId>`
- **THEN** the command SHALL return the effective variables for the run

#### Scenario: variables scoped to a stage

- **WHEN** a caller runs `mo workflow variables <runId> --stage plan`
- **THEN** the command SHALL return the effective variables resolved under the `plan` stage scope

#### Scenario: variables addressed by key path

- **WHEN** a caller runs `mo workflow variables <runId> --key some.nested.key`
- **THEN** the command SHALL return only the value at that key path within the effective variables

### Requirement: `events` lists the associated event stream

`mo workflow events <runId>` SHALL list the CloudEvent stream associated with the run (an associated resource, read-only), and SHALL support `--limit <n>` to bound the number of events returned. It is the workflowRunId-addressed equivalent of the existing issue-scoped events view; both SHALL be backed by the same event store.

#### Scenario: events lists the run's event stream

- **WHEN** a caller runs `mo workflow events <runId>`
- **THEN** the command SHALL return the CloudEvent stream associated with that run

#### Scenario: events honors a limit

- **WHEN** a caller runs `mo workflow events <runId> --limit 50`
- **THEN** the command SHALL bound the returned events to the requested limit

### Requirement: `list-sessions` lists sessions associated with the run, and single-session sub-actions stay issue-scoped

`mo workflow list-sessions <runId>` SHALL list the agent sessions associated with the run (an associated resource; list only). This change SHALL NOT introduce workflowRunId-direct entry points for single-session sub-actions (`show` / `transcript` / `compact` / `reset` / `followup`) — those continue to be reachable only via the issue-scoped `mo issue session ...` commands. Whether single-session sub-actions need a workflowRunId entry is deferred to a later issue and is not presumed here.

#### Scenario: list-sessions lists the run's sessions

- **WHEN** a caller runs `mo workflow list-sessions <runId>`
- **THEN** the command SHALL return the agent sessions associated with that run

#### Scenario: Single-session sub-actions are not added under workflow

- **WHEN** the `mo workflow` group is introspected after this change
- **THEN** the group SHALL NOT expose single-session sub-action commands (`show`/`transcript`/`compact`/`reset`/`followup` on an individual session) addressed by workflowRunId
- **AND** those sub-actions SHALL remain reachable only via the issue-scoped `mo issue session ...` commands

### Requirement: Read commands obey the shared CLI command-factory conventions and surface structured errors

Each read command SHALL be constructed through the same shared factory / option helpers as other `mo` read commands, so that the global output-format (`-o`) convention applies uniformly. Read commands SHALL surface structured server errors (message + code) on stderr and exit non-zero on failure (e.g. a run id that cannot be found), consistent with the rest of the CLI.

#### Scenario: An unknown run id is surfaced as an error

- **WHEN** a caller runs a read command with a `workflowRunId` the server cannot find
- **THEN** the CLI SHALL print the server's error on stderr and exit non-zero

#### Scenario: Output format is honored on read commands

- **WHEN** a caller passes `-o json` (or another supported format) to a read command
- **THEN** the command SHALL render its result through the shared output-format path used by the rest of the CLI
