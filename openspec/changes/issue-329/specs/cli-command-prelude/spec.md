### Requirement: Single shared output-mode validation prelude

The output-mode validation prelude — invoking the output-mode validator against the `--output` option value, writing the invalid message to error output, and returning the resolved mode together with the resulting exit code — SHALL be defined exactly once as a shared helper. Every resource command partial (Issue / Epic / Agent / Workflow / Project / ProjectWorkflow / Label / System / Server / Repository / Opencode, etc.) MUST consume that shared helper rather than each partial redefining its own `ValidateOutput` wrapper or inlining a `MohistCliApi.ValidateOutputMode` + `OutputModeResult.Invalid` pattern match. There MUST NOT remain a per-resource `ValidateOutput(MohistCliApi, string?)` definition (such as the ones previously duplicated in `IssueCommands` and `EpicCommands`), nor a scattered inline call to `MohistCliApi.ValidateOutputMode`.

#### Scenario: Blank or json output resolves to json mode

- **WHEN** a resource command is invoked with `--output` unset, blank, or `json`
- **THEN** the shared prelude resolves the mode to `json` and returns exit code 0

#### Scenario: table output resolves to table mode

- **WHEN** a resource command is invoked with `--output table`
- **THEN** the shared prelude resolves the mode to `table` and returns exit code 0

#### Scenario: Invalid output is rejected with exit code 1

- **WHEN** a resource command is invoked with an `--output` value other than `table` or `json`
- **THEN** the shared prelude writes a message stating `--output must be 'table' or 'json' (got '<value>')` to error output and yields a non-zero exit code (1)

#### Scenario: All command partials share one implementation

- **WHEN** the CLI command partials are inspected for output-mode validation
- **THEN** the validation-then-exit wrapper appears in exactly one shared location and is invoked by every former call site
- **AND** no per-resource `ValidateOutput` definition or inline `MohistCliApi.ValidateOutputMode` + `Invalid` pattern match remains duplicated across partials

### Requirement: Single shared project-reference resolution prelude

The project-reference resolution prelude — resolving a project identifier from `--project`, `--project-id`, or the persisted active-project state into a concrete project id, reporting the no-active-project error, and surfacing the result as a project-id-or-exit-code tuple — SHALL be defined exactly once as a shared helper consumed by every resource command that needs a project. The per-resource `ResolveProjectId(MohistCliApi, string?, string?)` wrapper previously duplicated in `IssueCommands` MUST be removed in favor of the shared helper. The resolution semantics and the no-active-project exit code (1) MUST remain unchanged.

#### Scenario: Explicit --project takes precedence

- **WHEN** a command is invoked with `--project <name>`
- **THEN** the shared prelude resolves and returns that project identifier with exit code 0

#### Scenario: Explicit --project-id takes precedence

- **WHEN** a command is invoked with `--project-id <id>`
- **THEN** the shared prelude resolves and returns that project id with exit code 0

#### Scenario: Conflicting --project and --project-id is rejected

- **WHEN** a command is invoked with both `--project` and `--project-id` that resolve to different values
- **THEN** the shared prelude writes a conflict message and returns exit code 1

#### Scenario: Missing project falls back to active project state

- **WHEN** a command is invoked with neither `--project` nor `--project-id`
- **AND** a persisted active-project id exists
- **THEN** the shared prelude returns that active project id with exit code 0

#### Scenario: No active project yields a non-zero exit

- **WHEN** a command is invoked with neither `--project` nor `--project-id`
- **AND** no active-project state exists (or it is unreadable/blank)
- **THEN** the shared prelude writes the no-active-project message and returns exit code 1

#### Scenario: All command partials share one resolution helper

- **WHEN** the CLI command partials are inspected for project-reference resolution
- **THEN** the resolve-then-exit wrapper appears in exactly one shared location and is invoked by every former call site
- **AND** no per-resource `ResolveProjectId` definition remains duplicated across partials
