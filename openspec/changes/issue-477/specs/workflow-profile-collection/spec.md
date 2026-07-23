### Requirement: Project-scoped workflow profile collection
A Project SHALL own a collection of WorkflowProfiles. `mo workflow list`, `mo workflow view`, `mo workflow create`, `mo workflow edit`, and `mo workflow delete` SHALL manage and read the current Project's collection exclusively. A custom Profile ID SHALL be stable for the life of that Profile within its Project, and the collection MUST permit `/` in a Profile ID.

#### Scenario: Create and retrieve a slash-capable custom Profile
- **WHEN** a user creates a Profile with ID `delivery/review` in a Project and then lists or views workflows for that Project
- **THEN** the collection SHALL contain `delivery/review`, and viewing it SHALL resolve that same Profile ID

#### Scenario: Same Profile ID in separate Projects
- **WHEN** two Projects each contain a custom Profile with the same ID
- **THEN** each `mo workflow` command SHALL operate only on the Profile in its selected current Project

### Requirement: WorkflowProfile command ownership
`mo workflow` SHALL be the sole Profile-management command surface. Legacy project-template and Profile-management commands MUST NOT provide a separate Profile collection contract.

#### Scenario: Manage a Profile through the supported command surface
- **WHEN** a user needs to list, view, create, edit, or delete a WorkflowProfile
- **THEN** the user SHALL perform the operation through the corresponding `mo workflow` command

### Requirement: Built-in Profiles in the collection
Built-in `mohist/*` Profiles SHALL appear in every Project's workflow collection with custom Profiles. A built-in Profile SHALL be readable and selectable wherever a Profile from the collection can be selected, but it MUST NOT be editable or deletable.

#### Scenario: View a built-in Profile
- **WHEN** a user views `mohist/github-pr` through `mo workflow view` for a Project
- **THEN** the command SHALL return the built-in Profile's details and Definition

#### Scenario: Attempt to mutate a built-in Profile
- **WHEN** a user runs `mo workflow edit` or `mo workflow delete` for a built-in `mohist/*` Profile
- **THEN** the operation SHALL fail with a domain error that identifies the Profile as read-only

### Requirement: Definition YAML view
`mo workflow view <profile> --yaml` SHALL return the selected Profile's raw Definition as YAML. The `--yaml` option MUST be mutually exclusive with JSON field selection and MUST NOT establish a general YAML output mode for workflow commands.

#### Scenario: Request a Profile Definition as YAML
- **WHEN** a user runs `mo workflow view delivery/review --yaml`
- **THEN** the command SHALL return the `delivery/review` Definition in YAML form rather than a rendered Profile summary

#### Scenario: Combine YAML with field-selected JSON output
- **WHEN** a user supplies `--yaml` and a JSON field-selection option to `mo workflow view`
- **THEN** the CLI SHALL reject the invocation before requesting the Profile

### Requirement: Authoritative save validation
Creating or editing a custom Profile SHALL validate its Definition with the authoritative Workflow Definition semantics and SHALL validate its `uses` and `with` values against the reported Action catalog. The save operation MUST reject invalid input and MUST identify whether a reported validation failure originates from Definition validation or Action-contract validation. The CLI MUST NOT implement a duplicate Definition or Action-contract validator.

#### Scenario: Save an invalid Definition
- **WHEN** a user creates or edits a Profile with a Definition that violates the authoritative Definition rules
- **THEN** the Profile SHALL not be saved and the failure SHALL identify the Definition validation source

#### Scenario: Save an Action-contract violation
- **WHEN** a user creates or edits a Profile whose Definition uses an unavailable Action or invalid Action input
- **THEN** the Profile SHALL not be saved and the failure SHALL identify the Action-contract validation source

### Requirement: Protected Profile deletion
`mo workflow delete` SHALL refuse to delete a custom Profile that is referenced by the Project default, by any Issue in that Project, or by any active WorkflowRun. The refusal MUST identify each blocking reference relationship. An unreferenced custom Profile SHALL be deletable. Profile deletion and every operation that writes a Project default, an Issue selection, or a WorkflowRun binding SHALL be serialized for that Project, so they cannot leave a reference to a deleted Profile.

#### Scenario: Delete a Profile used as the Project default
- **WHEN** a user attempts to delete the Profile selected as a Project's default
- **THEN** deletion SHALL fail and the error SHALL identify the Project default reference

#### Scenario: Delete a Profile referenced by an Issue and active Run
- **WHEN** a user attempts to delete a Profile referenced by an Issue and by an active WorkflowRun
- **THEN** deletion SHALL fail and the error SHALL identify both the Issue and active WorkflowRun reference relationships

#### Scenario: Delete an unreferenced custom Profile
- **WHEN** a user deletes a custom Profile with no Project default, Issue, or active WorkflowRun reference
- **THEN** the Profile SHALL be removed from that Project's collection

#### Scenario: Reference write precedes deletion
- **WHEN** an Issue selection or WorkflowRun binding to a custom Profile is accepted before a deletion request for that Profile is processed
- **THEN** deletion SHALL fail and identify the newly committed reference as a blocker

#### Scenario: Deletion precedes a reference write
- **WHEN** deletion of an unreferenced custom Profile is accepted before an Issue selection, Project default, or WorkflowRun binding request for that Profile is processed
- **THEN** the later reference write SHALL fail with the retryable `workflow-profile-not-found` conflict and SHALL not create a dangling reference
