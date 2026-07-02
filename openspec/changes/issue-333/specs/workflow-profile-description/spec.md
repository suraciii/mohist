### Requirement: System workflow profile description is sourced solely from the workflow YAML

Every system workflow profile's user-facing description SHALL be sourced solely from the workflow YAML `description` field via the profile's parsed `WorkflowDefinition`. No system workflow profile SHALL expose a description read from a compiled-in C# string constant. Both built-in profiles (`mohist/local`, `mohist/github-pr`) SHALL resolve their `Description` from their respective `WorkflowDefinition.Description` (`MohistWorkflow.Definition` and `MohistWorkflow.GithubPrWorkflowDefinition`), exactly as `mohist/local` already does. The `MohistGithubPrIssueWorkflowProfile.GithubPrDescription` constant and every reference to it SHALL be removed.

#### Scenario: mohist/local description reads from the local workflow YAML

- **WHEN** the `mohist/local` profile's `Description` is read
- **THEN** the value SHALL equal the `description` field parsed from `mohist-local.workflow.yaml` (`MohistWorkflow.Definition.Description`)

#### Scenario: mohist/github-pr description reads from the github-pr workflow YAML

- **WHEN** the `mohist/github-pr` profile's `Description` is read
- **THEN** the value SHALL equal the `description` field parsed from `mohist-github-pr.workflow.yaml` (`MohistWorkflow.GithubPrWorkflowDefinition.Description`)
- **AND** the value SHALL NOT be sourced from any C# string constant compiled into the binary

#### Scenario: no compiled-in description constant remains

- **WHEN** the `MohistGithubPrIssueWorkflowProfile` type is inspected
- **THEN** it SHALL NOT declare a `GithubPrDescription` constant
- **AND** no code path SHALL reference a `GithubPrDescription` constant to produce a profile description

### Requirement: An empty workflow YAML description falls back to a placeholder

When a system workflow's parsed YAML `description` is null, empty, or whitespace, the profile's resolved description SHALL fall back to the same placeholder text used by `mohist/local` (`"No description provided"`). Both built-in profiles SHALL apply this fallback identically.

#### Scenario: blank YAML description yields the placeholder

- **WHEN** a system workflow profile's parsed `WorkflowDefinition.Description` is null, empty, or whitespace
- **THEN** the profile's resolved `Description` SHALL be `"No description provided"`

#### Scenario: non-blank YAML description is used verbatim

- **WHEN** a system workflow profile's parsed `WorkflowDefinition.Description` is non-blank
- **THEN** the profile's resolved `Description` SHALL be that YAML value (with trailing whitespace trimmed) and SHALL NOT be the placeholder

### Requirement: Both catalog materialization paths read the description identically

The two description-bearing catalog materialization paths — `SystemTemplateInfo` produced by `ProjectWorkflowProfileManager.BuildSystemTemplates()` and `WorkflowProfileDescription` produced by `IssueWorkflowProfileRegistry.ListDescribed()` — SHALL each source the description from the profile's `WorkflowDefinition.Description` (with the shared empty-value fallback). The description value exposed by `BuildSystemTemplates()` for a given profile SHALL equal both the profile instance's `Description` and the `ListDescribed()` entry for the same profile id. The `BuildSystemTemplates()` branch for `mohist/github-pr` SHALL NOT reference the removed C# constant and SHALL read its description the same way the `mohist/local` branch does.

#### Scenario: SystemTemplateInfo description matches the profile instance

- **WHEN** `BuildSystemTemplates()` materializes the `SystemTemplateInfo` for `mohist/local` and `mohist/github-pr`
- **THEN** each template's `Description` SHALL equal the corresponding profile instance's `Description`

#### Scenario: WorkflowProfileDescription from ListDescribed matches the profile instance

- **WHEN** `ListDescribed()` materializes the `WorkflowProfileDescription` entries
- **THEN** each entry's `Description` SHALL equal the corresponding profile instance's `Description`

#### Scenario: the two catalog paths agree with each other

- **WHEN** the `SystemTemplateInfo` and `WorkflowProfileDescription` for the same profile id are compared
- **THEN** their `Description` values SHALL be equal

### Requirement: The workflow-profile model carries no structured SuitableFor field

The workflow-profile model SHALL NOT carry a `SuitableFor` structured tag field. The `IIssueWorkflowProfile.SuitableFor` property, the `MohistIssueWorkflowProfileBase` abstract member, and both built-in profile overrides SHALL be removed. The natural-language `description` SHALL be the sole applicability description for a profile.

#### Scenario: the profile interface exposes no SuitableFor member

- **WHEN** `IIssueWorkflowProfile` and `MohistIssueWorkflowProfileBase` are inspected
- **THEN** neither SHALL declare a `SuitableFor` member
- **AND** neither built-in profile (`mohist/local`, `mohist/github-pr`) SHALL override a `SuitableFor` member

#### Scenario: no SuitableFor matcher or registry match method remains

- **WHEN** the server source is inspected for tag-based profile matching
- **THEN** the `SuitableForMatcher` type SHALL NOT exist
- **AND** `IssueWorkflowProfileRegistry` SHALL NOT expose a `Matches` method

### Requirement: The described workflow-profile surface exposes no suitableFor data

The `suitableFor` field SHALL leave the workflow-profile description surface. The `WorkflowProfileDescription` DTO SHALL NOT carry a `SuitableFor` member, so the `/api/workflow-profiles` response SHALL NOT serialize a `suitableFor` field. The `mo workflow list --described` output SHALL NOT print a `Suitable for:` line (nor a `(not specified)` placeholder); only the description SHALL be shown. The `--described` option help text SHALL NOT reference `suitable_for`.

#### Scenario: the workflow-profiles API response omits suitableFor

- **WHEN** `GET /api/workflow-profiles` returns its profile entries
- **THEN** no entry SHALL contain a `suitableFor` field (serialized or otherwise)

#### Scenario: WorkflowProfileDescription DTO omits SuitableFor

- **WHEN** the `WorkflowProfileDescription` record is inspected
- **THEN** it SHALL expose only `Id`, `DisplayName`, and `Description`
- **AND** it SHALL NOT expose a `SuitableFor` member

#### Scenario: described CLI output shows only the description

- **WHEN** `mo workflow list --described` renders a profile
- **THEN** the output SHALL contain the profile id, display name, and description
- **AND** the output SHALL NOT contain a `Suitable for:` line or a `(not specified)` placeholder

#### Scenario: described option help text drops suitable_for wording

- **WHEN** the `--described` option description for `mo workflow list` is read
- **THEN** the text SHALL NOT mention `suitable_for`

### Requirement: Issue-creation workflow selection does not match on suitable_for tags

The bundled `mohist-create-issue` skill SHALL NOT select or recommend a workflow profile by matching `suitable_for` tags. Workflow selection SHALL use the default profile or operator choice over the natural-language description, with the chosen `recommended_workflow` still sourced from an enabled id returned by `mo workflow list --described`.

#### Scenario: skill guidance uses default or operator choice, not tag matching

- **WHEN** the `mohist-create-issue` skill documentation describes how to pick `recommended_workflow`
- **THEN** the guidance SHALL NOT instruct matching against `suitable_for` tags
- **AND** SHALL instead select the default profile or an operator-chosen enabled profile id
