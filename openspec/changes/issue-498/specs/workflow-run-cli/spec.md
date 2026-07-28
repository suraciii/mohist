### Requirement: WorkflowRun reads use the Run command surface
The CLI SHALL expose WorkflowRun status and detail through `mo run view`. A caller SHALL target the Run by exactly one of a positional Run ID or `--issue <number>`; `--issue` SHALL resolve the Issue's bound WorkflowRun in the selected Project.

#### Scenario: View a Run by Issue number
- **WHEN** a caller invokes `mo run view --issue 42`
- **THEN** the CLI SHALL resolve Issue 42's bound WorkflowRun and display that Run's detail

#### Scenario: Reject ambiguous Run targets
- **WHEN** a caller supplies both a Run ID and `--issue` to `mo run view`
- **THEN** the CLI SHALL reject the invocation as a usage error without reading either target

### Requirement: Workflow timeline is a Run read
The CLI SHALL expose the existing WorkflowRun timeline through `mo run timeline`. The command SHALL preserve the timeline information available before this change and SHALL accept the same Run ID or `--issue <number>` target forms as other Run-specific reads.

#### Scenario: Read a timeline by Issue number
- **WHEN** a caller invokes `mo run timeline --issue 42`
- **THEN** the CLI SHALL display the timeline for Issue 42's bound WorkflowRun

#### Scenario: Read a timeline by Run ID
- **WHEN** a caller invokes `mo run timeline wr_abc123`
- **THEN** the CLI SHALL display the timeline for WorkflowRun `wr_abc123`

### Requirement: Issue workflow runtime reads are retired
The `mo issue workflow` command area SHALL NOT be present in the CLI command tree. `mo issue workflow status` and `mo issue workflow timeline` SHALL be rejected as unknown Issue commands; the CLI SHALL NOT retain them as aliases or compatibility paths.

#### Scenario: Reject the retired Issue workflow status command
- **WHEN** a caller invokes `mo issue workflow status 42`
- **THEN** the CLI SHALL return a usage error without reading workflow status

#### Scenario: Reject the retired Issue workflow timeline command
- **WHEN** a caller invokes `mo issue workflow timeline 42`
- **THEN** the CLI SHALL return a usage error without reading a workflow timeline

### Requirement: Command discovery reflects ownership boundaries
The `mo issue` group help and CLI reference SHALL describe Issue lifecycle and Issue-owned configuration without presenting a WorkflowRun read subarea. The `mo run` group help and CLI reference SHALL list both Run detail and timeline reads. Workflow Profile selection SHALL remain available only through `mo issue create/edit --workflow-profile` and `mo issue edit --inherit-workflow-profile`.

#### Scenario: Discover Run reads from group help
- **WHEN** a caller requests `mo run --help`
- **THEN** the help SHALL list `view` and `timeline` as Run reads

#### Scenario: Discover Profile selection from Issue commands
- **WHEN** a caller requests help for `mo issue create` or `mo issue edit`
- **THEN** the help SHALL expose the applicable Workflow Profile selection option
- **AND** `mo issue --help` SHALL NOT direct callers to an Issue workflow subarea for Profile selection
