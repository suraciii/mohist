### Requirement: Both built-in workflow profiles resolve their description from the workflow YAML as the single source of truth

The `mohist/local` and `mohist/github-pr` built-in system workflow profiles SHALL resolve the user-facing description surfaced through `IIssueWorkflowProfile.Description` solely from the `description` field of their respective workflow YAML (parsed into `WorkflowDefinition.Description`). Neither profile SHALL surface a description sourced from a parallel compiled C# string constant, so the two profiles SHALL NOT maintain divergent description sources that can drift in wording.

#### Scenario: github-pr profile description comes from the parsed YAML, not a C# constant

- **WHEN** the `mohist/github-pr` profile's `Description` is read
- **THEN** it SHALL equal the `description` parsed from `mohist-github-pr.workflow.yaml` into `MohistWorkflow.GithubPrWorkflowDefinition.Description`
- **AND** it SHALL NOT be sourced from a `public const string GithubPrDescription` compiled into the binary

#### Scenario: local profile description comes from the parsed YAML

- **WHEN** the `mohist/local` profile's `Description` is read
- **THEN** it SHALL equal the `description` parsed from `mohist-local.workflow.yaml` into `MohistWorkflow.Definition.Description`

#### Scenario: no parallel compiled constant shadows the YAML description

- **WHEN** the `mohist/github-pr` profile assembly is inspected
- **THEN** there SHALL be no `public const string GithubPrDescription` on `MohistGithubPrIssueWorkflowProfile`
- **AND** no built-in profile SHALL carry a parallel compiled description string that diverges from its YAML `description`

### Requirement: Both profiles apply the same empty-value fallback for a blank YAML description

When the parsed workflow YAML `description` is null, empty, or whitespace, both built-in profiles SHALL surface a consistent fallback placeholder ("No description provided") using the same resolution pattern, rather than emitting the raw blank value or throwing.

#### Scenario: blank github-pr YAML description falls back to the placeholder

- **WHEN** `GithubPrWorkflowDefinition.Description` is null, empty, or whitespace
- **THEN** the `mohist/github-pr` profile's `Description` SHALL return "No description provided"
- **AND** it SHALL NOT return the raw blank value or throw

#### Scenario: blank local YAML description falls back to the placeholder

- **WHEN** `MohistWorkflow.Definition.Description` (local) is null, empty, or whitespace
- **THEN** the `mohist/local` profile's `Description` SHALL return "No description provided"

#### Scenario: both profiles share one fallback pattern

- **WHEN** either built-in profile resolves a blank description
- **THEN** the fallback value and resolution logic SHALL be identical for `mohist/local` and `mohist/github-pr`

### Requirement: ProjectWorkflowProfileManager assembles both system templates' descriptions from the parsed YAML with the identical pattern

`ProjectWorkflowProfileManager.BuildSystemTemplates()` SHALL build the `SystemTemplateInfo.Description` for both `mohist/local` and `mohist/github-pr` from the corresponding parsed `WorkflowDefinition.Description`, applying the same empty-value fallback. The local and github-pr branches SHALL assemble the description identically; the github-pr branch SHALL NOT reference the deleted `GithubPrDescription` constant. Each `SystemTemplateInfo.Description` SHALL be sourced from the same YAML `description` as its profile's `Description`.

#### Scenario: both system templates read their description from the parsed YAML

- **WHEN** `BuildSystemTemplates()` assembles the system template catalog
- **THEN** the `mohist/github-pr` template's `Description` SHALL come from `GithubPrWorkflowDefinition.Description`
- **AND** the `mohist/local` template's `Description` SHALL come from the local `Definition.Description`
- **AND** the github-pr branch SHALL NOT reference `MohistGithubPrIssueWorkflowProfile.GithubPrDescription`

#### Scenario: local and github-pr branches assemble the description identically

- **WHEN** the two branches build their `SystemTemplateInfo.Description`
- **THEN** they SHALL use the same resolution logic (read the parsed `WorkflowDefinition.Description`, then apply the same empty fallback)
- **AND** neither branch SHALL depend on a profile-specific compiled constant

#### Scenario: blank YAML description yields the placeholder for a system template

- **WHEN** a built-in profile's parsed `WorkflowDefinition.Description` is null, empty, or whitespace
- **THEN** that profile's `SystemTemplateInfo.Description` SHALL be "No description provided"

### Requirement: The resolved github-pr description surfaces the gh CLI prerequisite and GitHub PR wording from the YAML

Because the YAML `description` is the single source, the tokens downstream consumers and specs rely on — the `gh` CLI prerequisite (`gh`, `gh auth login`) and the "GitHub PR" wording — SHALL appear identically in both the `mohist/github-pr` profile's `Description` and the github-pr `SystemTemplateInfo.Description`, sourced from `mohist-github-pr.workflow.yaml`.

#### Scenario: gh CLI prerequisite tokens are present in the resolved description

- **WHEN** the `mohist/github-pr` profile's `Description` is read
- **THEN** it SHALL contain "gh", "gh auth login", and "GitHub PR"

#### Scenario: the system template description matches the profile description source

- **WHEN** the github-pr `SystemTemplateInfo.Description` and the profile `Description` are compared
- **THEN** both SHALL be sourced from the same `mohist-github-pr.workflow.yaml` `description`
- **AND** both SHALL contain the "gh auth login" prerequisite
