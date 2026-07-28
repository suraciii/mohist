### Requirement: WorkflowRun reads use the Run command surface
The CLI SHALL expose WorkflowRun status and detail through `mo run view`. A caller SHALL target the Run by exactly one of a positional Run ID or `--issue <number>`; `--issue` SHALL resolve the Issue's bound WorkflowRun in the selected Project.

#### Scenario: View a Run by Issue number
- **WHEN** a caller invokes `mo run view --issue 42`
- **THEN** the CLI SHALL resolve Issue 42's bound WorkflowRun and display that Run's detail

#### Scenario: Reject ambiguous Run targets
- **WHEN** a caller supplies both a Run ID and `--issue` to `mo run view`
- **THEN** the CLI SHALL reject the invocation as a usage error without reading either target

### Requirement: Run detail remains the only stage-progression read
`mo run view` SHALL remain the only Run command that presents a WorkflowRun's ordered stage progression. The CLI SHALL NOT expose `mo run timeline`, because it would duplicate the stages already returned by Run detail without a distinct timeline resource or output contract.

#### Scenario: Reject a duplicate Run timeline command
- **WHEN** a caller invokes `mo run timeline wr_abc123`
- **THEN** the CLI SHALL return a usage error without reading the Run

### Requirement: Issue workflow runtime reads are retired
The `mo issue workflow` command area SHALL NOT be present in the CLI command tree. `mo issue workflow status` and `mo issue workflow timeline` SHALL be rejected as unknown Issue commands; the CLI SHALL NOT retain them as aliases or compatibility paths.

#### Scenario: Reject the retired Issue workflow status command
- **WHEN** a caller invokes `mo issue workflow status 42`
- **THEN** the CLI SHALL return a usage error without reading workflow status

#### Scenario: Reject the retired Issue workflow timeline command
- **WHEN** a caller invokes `mo issue workflow timeline 42`
- **THEN** the CLI SHALL return a usage error without reading a workflow timeline

### Requirement: Command discovery reflects ownership boundaries
The `mo issue` group help and CLI reference SHALL describe Issue lifecycle and Issue-owned configuration without presenting a WorkflowRun read subarea. The `mo run` group help and CLI reference SHALL list `view` as the Run detail read and SHALL NOT list `timeline`. Workflow Profile selection SHALL remain available only through `mo issue create/edit --workflow-profile` and `mo issue edit --inherit-workflow-profile`.

#### Scenario: Discover the sole Run detail read from group help
- **WHEN** a caller requests `mo run --help`
- **THEN** the help SHALL list `view` as a Run read
- **AND** SHALL NOT list `timeline`

#### Scenario: Discover Profile selection from Issue commands
- **WHEN** a caller requests help for `mo issue create` or `mo issue edit`
- **THEN** the help SHALL expose the applicable Workflow Profile selection option
- **AND** `mo issue --help` SHALL NOT direct callers to an Issue workflow subarea for Profile selection
