### Requirement: `run list` enumerates WorkflowRuns

`mo run list` SHALL return a collection of WorkflowRuns visible in the current project scope, each showing at minimum the Run ID, current status, current stage, and associated issue number. The command SHALL accept `--project` for project scoping and `--json <fields>` for field selection. An empty result SHALL exit 0.

#### Scenario: List runs in the active project

- **WHEN** the user runs `mo run list` with an active project that has runs
- **THEN** the CLI SHALL output a list of WorkflowRuns
- **AND** each entry SHALL include the Run ID, status, stage, and associated issue number

#### Scenario: List with no runs exits zero

- **WHEN** the user runs `mo run list` with a project that has no runs
- **THEN** the CLI SHALL exit 0

#### Scenario: List with field selection projects the collection

- **WHEN** the user runs `mo run list --json id,status,currentStage`
- **THEN** stdout SHALL contain a JSON array where each element has only the requested fields

### Requirement: `run view` shows full WorkflowRun detail

`mo run view` SHALL display the full WorkflowRun resource — status, stages, approval state, and associated issue. It SHALL accept a positional Run ID or `--issue <number>` as target, following the same mutual-exclusion contract as control verbs. It SHALL support `--json <fields>` for field selection.

#### Scenario: View by Run ID

- **WHEN** the user runs `mo run view wr_abc123`
- **THEN** the CLI SHALL GET `/api/workflow-runs/wr_abc123`
- **AND** SHALL render the run's status, stages, and approval state

#### Scenario: View by issue selector

- **WHEN** the user runs `mo run view --issue 42` and issue 42 is bound to `wr_abc123`
- **THEN** the CLI SHALL resolve `wr_abc123` from the issue
- **AND** SHALL GET `/api/workflow-runs/wr_abc123`

#### Scenario: View with field selection

- **WHEN** the user runs `mo run view wr_abc123 --json id,status,currentStage`
- **THEN** stdout SHALL contain a JSON object with only the requested fields

#### Scenario: View with both Run ID and --issue fails locally

- **WHEN** the user runs `mo run view wr_abc123 --issue 42`
- **THEN** the CLI SHALL exit with code 2
- **AND** no HTTP request SHALL be issued

### Requirement: `run view --yaml` renders the Workflow Definition

`mo run view --yaml` SHALL fetch and print the rendered Workflow Definition YAML source for the targeted Run. `--yaml` SHALL be mutually exclusive with `--json`; using both SHALL fail with exit code 2 and SHALL NOT issue an HTTP request.

#### Scenario: View YAML for a run

- **WHEN** the user runs `mo run view wr_abc123 --yaml`
- **THEN** the CLI SHALL GET `/api/workflow-runs/wr_abc123/yaml`
- **AND** stdout SHALL contain the rendered YAML source

#### Scenario: --yaml and --json are mutually exclusive

- **WHEN** the user runs `mo run view wr_abc123 --yaml --json id`
- **THEN** the CLI SHALL exit with code 2
- **AND** no HTTP request SHALL be issued

### Requirement: `run watch` streams run progress until terminal

`mo run watch` SHALL follow the targeted Run's progress, printing updates as they arrive, and SHALL terminate when the Run reaches a terminal state (completed, stopped, or cancelled) or when the user interrupts. It SHALL accept a positional Run ID or `--issue <number>` as target. The output format for streaming updates SHALL be one JSON object per line (NDJSON) so that automation can parse incremental progress.

#### Scenario: Watch streams updates until the run completes

- **WHEN** the user runs `mo run watch wr_abc123` and the run eventually completes
- **THEN** the CLI SHALL stream progress updates to stdout
- **AND** SHALL exit 0 when the run reaches a terminal state

#### Scenario: Watch by issue selector

- **WHEN** the user runs `mo run watch --issue 42` and issue 42 is bound to `wr_abc123`
- **THEN** the CLI SHALL resolve `wr_abc123` and stream its progress

#### Scenario: Watch with both targets fails locally

- **WHEN** the user runs `mo run watch wr_abc123 --issue 42`
- **THEN** the CLI SHALL exit with code 2
- **AND** no HTTP request SHALL be issued
