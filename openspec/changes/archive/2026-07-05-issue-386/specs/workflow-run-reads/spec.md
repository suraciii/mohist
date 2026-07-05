### Requirement: `get` is the canonical single-resource read for a workflow run

The `mo workflow` command surface SHALL expose `mo workflow get <runId>` as the canonical command for fetching a single workflow run resource. `get` SHALL accept the output-format option `-o table|json|yaml` with the same semantics as today's read: the default table output renders the summary view (status, stage progress, approval state, associated issue); `-o json` renders the full read model; `-o yaml` renders the workflow template definition by hitting `GET /api/workflow-runs/{runId}/yaml` (output format MUST NOT create a separate command). `get` SHALL require the `<runId>` argument and SHALL fail locally with a non-zero exit when it is missing.

#### Scenario: `get` fetches the full run resource and renders the default summary table

- **WHEN** a caller runs `mo workflow get <runId>` with no `-o` flag
- **THEN** the CLI SHALL GET `/api/workflow-runs/{runId}` and render the `WorkflowRunDetail` table shape
- **AND** the rendered summary SHALL include status, stage progress, approval state, and associated issue

#### Scenario: `get -o json` renders the full read model

- **WHEN** a caller runs `mo workflow get <runId> -o json`
- **THEN** the CLI SHALL GET `/api/workflow-runs/{runId}` and print the full resource as JSON

#### Scenario: `get -o yaml` renders the workflow template definition

- **WHEN** a caller runs `mo workflow get <runId> -o yaml`
- **THEN** the CLI SHALL GET `/api/workflow-runs/{runId}/yaml` and print the rendered template-definition YAML
- **AND** SHALL NOT GET the JSON read model

#### Scenario: Missing run id fails locally

- **WHEN** a caller runs `mo workflow get` with no run id
- **THEN** the CLI SHALL print a `<run-id> is required` error to stderr and exit non-zero without sending a request

### Requirement: `show` is a transitional alias of `get`

`mo workflow show <runId>` SHALL be retained as a transitional alias of `get` with identical arguments, output-format behavior (including the `-o yaml` template-definition contract), and exit codes. The alias exists solely to keep scripts written against the previously-landed surface working; it MUST NOT diverge in behavior from `get`.

#### Scenario: `show` behaves identically to `get`

- **WHEN** a caller runs `mo workflow show <runId>` with any combination of `-o table|json|yaml`
- **THEN** the CLI SHALL produce the same output and exit code as `mo workflow get <runId>` with the same flags

### Requirement: The redundant `status` command is removed

The `mo workflow status <runId>` command SHALL NOT exist. Its compact projection was a strict subset of the same GET that `get` performs, and `get`'s default table output already is the summary view, so `status` is a redundant command (not a rename) and MUST be deleted rather than aliased.

#### Scenario: `status` is not a registered command

- **WHEN** the `mo workflow` command group is built
- **THEN** the group SHALL NOT contain a `status` subcommand
- **AND** `mo workflow status <runId>` SHALL fail as an unknown command
