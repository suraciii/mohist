## ADDED Requirements

### Requirement: prompts.* references resolve from the merged system+project map at workflow start

When a workflow definition (or any stage's task) references `prompts.<key>`, the server SHALL resolve `<key>` from the merged `prompts` dictionary for the issue's project. The merged dictionary is produced by overlaying project overrides on the system template set, with project overrides fully replacing the system body for matching keys and project-unique keys adding new entries.

#### Scenario: Workflow YAML referencing a known system key resolves to system body

- **WHEN** a workflow task contains `prompt: ${{ prompts.proposal }}`
- **AND** the system has a `proposal` template
- **AND** the project has no override for `proposal`
- **THEN** `${{ prompts.proposal }}` resolves to the system `proposal` body
- **AND** the workflow starts and dispatches the task with the system body

#### Scenario: Workflow YAML referencing an overridden system key resolves to project body

- **WHEN** a workflow task contains `prompt: ${{ prompts.proposal }}`
- **AND** the project has an override row for `proposal`
- **THEN** `${{ prompts.proposal }}` resolves to the project override body
- **AND** the system body is not used for that issue

#### Scenario: Workflow YAML referencing a project-unique key resolves to project body

- **WHEN** a workflow task contains `prompt: ${{ prompts.deploy-checklist }}`
- **AND** the system has no `deploy-checklist` template
- **AND** the project has an override row for `deploy-checklist`
- **THEN** `${{ prompts.deploy-checklist }}` resolves to the project override body

### Requirement: workflow start-work fails with HTTP 400 when prompts.* keys are unknown

The start-work API SHALL validate that every `prompts.<key>` referenced by the workflow definition exists in the merged `prompts` dictionary. If any referenced key is missing, the API SHALL return HTTP 400 with code `missing_prompts` and the list of missing keys in `details.missingKeys`.

#### Scenario: Unknown template key fails start-work with 400

- **WHEN** a workflow task contains `prompt: ${{ prompts.does-not-exist }}`
- **AND** the merged `prompts` dictionary has no `does-not-exist` key
- **THEN** `POST /api/issues/{n}/start` returns HTTP 400
- **AND** the response body has `code: "missing_prompts"`
- **AND** `details.missingKeys` includes `"does-not-exist"`
- **AND** no workflow run is created

#### Scenario: Project override makes a previously-unknown key resolvable

- **WHEN** a workflow task references `prompts.deploy-checklist`
- **AND** the project has an override row for `deploy-checklist`
- **THEN** start-work proceeds without the `missing_prompts` error
- **AND** the dispatch uses the project body

#### Scenario: Multiple missing keys are reported together

- **WHEN** a workflow task references `prompts.alpha` and `prompts.beta`
- **AND** neither key exists in the merged map
- **THEN** start-work returns HTTP 400 with `missing_prompts`
- **AND** `details.missingKeys` includes both `"alpha"` and `"beta"`

### Requirement: prompts.* body is interpolated by the same engine semantics as the runner

The body returned for `prompts.<key>` in the workflow variables SHALL be the result of applying the runner-compatible interpolation rules to the merged body: `${{ path.to.value }}` references resolve against the issue's effective variables, unresolvable references remain in the output, and recursive expansion runs up to 5 passes. The server SHALL NOT pre-render the body itself; the runner's `renderTemplate` continues to perform the final expansion.

#### Scenario: Runner resolves prompts.* body against vars.*

- **WHEN** a workflow task receives `prompts.proposal = "Use ${{ openspecChangeDir }}"`
- **AND** the issue's variables include `openspecChangeDir`
- **THEN** the runner expands `${{ openspecChangeDir }}` to the variable value
- **AND** the final prompt sent to the agent contains the resolved value

#### Scenario: Missing variable leaves the token in place

- **WHEN** the merged body contains `${{ issue.priority }}` and the issue has no `priority` variable
- **THEN** the runner leaves `${{ issue.priority }}` in the output
- **AND** the agent receives the unresolved token (matching runner semantics)

#### Scenario: Recursive expansion respects the 5-pass cap

- **WHEN** a variable value contains another `${{ ... }}` token
- **THEN** the runner SHALL attempt up to 5 passes of expansion
- **AND** SHALL NOT loop infinitely on self-referential data
