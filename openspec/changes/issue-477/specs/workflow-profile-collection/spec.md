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
`mo workflow view <profile> --yaml` SHALL return the selected Profile's Definition source as YAML. A newly created or edited custom Profile SHALL return exactly its submitted source; a Profile migrated from legacy semantic storage SHALL return its documented canonical YAML rendering. The `--yaml` option MUST be mutually exclusive with JSON field selection and MUST NOT establish a general YAML output mode for workflow commands.

#### Scenario: Request a Profile Definition as YAML
- **WHEN** a user runs `mo workflow view delivery/review --yaml`
- **THEN** the command SHALL return the `delivery/review` Definition in YAML form rather than a rendered Profile summary

#### Scenario: Combine YAML with field-selected JSON output
- **WHEN** a user supplies `--yaml` and a JSON field-selection option to `mo workflow view`
- **THEN** the CLI SHALL reject the invocation before requesting the Profile

### Requirement: Legacy source and reserved-ID migration
The migration SHALL render and persist documented canonical YAML for every legacy custom or inline Definition that was stored only as semantic data; it SHALL preserve verbatim source for all Profiles created or edited after the migration. A legacy custom Profile ID in the reserved `mohist/*` namespace SHALL migrate to `legacy-reserved/{base64url-utf8(originalProfileId)}`. The migration SHALL rewrite every Project default, Issue selection, inline-derived Issue selection, and WorkflowRun binding in that Project to the migrated ID. If that deterministic target ID is occupied by a different legacy custom Profile, migration SHALL fail atomically with the Project, source ID, and target ID rather than silently overwrite or drop a Definition.

#### Scenario: View migrated legacy Definition source
- **WHEN** a user views a Profile migrated from a legacy semantic Definition with `--yaml`
- **THEN** the command SHALL return its persisted canonical YAML source and SHALL not claim to return the legacy submitted text

#### Scenario: Migrate a reserved legacy custom ID
- **WHEN** a Project has a legacy custom Profile `mohist/local` selected by its default, an Issue, or a WorkflowRun
- **THEN** migration SHALL create `legacy-reserved/bW9oaXN0L2xvY2Fs` and rewrite each such reference to that ID while `mohist/local` resolves to the built-in Profile

#### Scenario: Detect a reserved-ID migration target conflict
- **WHEN** the deterministic target for a legacy reserved custom Profile is occupied by a different legacy custom Profile in the same Project
- **THEN** migration SHALL fail without partial writes and identify the Project, source ID, and target ID

### Requirement: Authoritative save validation
Creating or editing a custom Profile SHALL validate its Definition with the authoritative Workflow Definition semantics and SHALL validate its `uses` and `with` values against the reported Action catalog. The save operation MUST reject invalid input and MUST identify whether a reported validation failure originates from Definition validation or Action-contract validation. The CLI MUST NOT implement a duplicate Definition or Action-contract validator.

#### Scenario: Save an invalid Definition
- **WHEN** a user creates or edits a Profile with a Definition that violates the authoritative Definition rules
- **THEN** the Profile SHALL not be saved and the failure SHALL identify the Definition validation source

#### Scenario: Save an Action-contract violation
- **WHEN** a user creates or edits a Profile whose Definition uses an unavailable Action or invalid Action input
- **THEN** the Profile SHALL not be saved and the failure SHALL identify the Action-contract validation source

### Requirement: Protected Profile deletion
`mo workflow delete` SHALL refuse to delete a custom Profile that is referenced by the Project default, by any Issue in that Project including a terminal Issue, or by any active WorkflowRun. The refusal MUST identify each blocking reference relationship. An unreferenced custom Profile SHALL be deletable. `WorkflowProfileReferenceCoordinator` SHALL serialize deletion with Project-default writes and WorkflowRun bindings. `IssueRepositoryCoordinatorGrain` SHALL continue to serialize Issue creation, repository lifecycle commands, and Issue explicit selection; Issue creation SHALL commit its repository binding and Profile selection together in its one Issue transaction. For custom Profiles, Project-default, Issue-selection, and Run-binding persistence rows SHALL carry a nullable custom-Profile key backing column with a restrictive `(ProjectId, ProfileId)` foreign key to the custom Profile record. It SHALL be populated only for custom IDs and remain null for built-ins, while the public and domain reference remains the single Profile ID. This final transactional constraint SHALL make a concurrent deletion and Issue selection resolve as either a committed reference that blocks deletion or a retryable `workflow-profile-not-found` conflict, never a dangling reference. Built-in Profiles need no foreign key because they are read-only and cannot be deleted.

#### Scenario: Delete a Profile used as the Project default
- **WHEN** a user attempts to delete the Profile selected as a Project's default
- **THEN** deletion SHALL fail and the error SHALL identify the Project default reference

#### Scenario: Delete a Profile referenced by an Issue and active Run
- **WHEN** a user attempts to delete a Profile referenced by an Issue and by an active WorkflowRun
- **THEN** deletion SHALL fail and the error SHALL identify both the Issue and active WorkflowRun reference relationships

#### Scenario: Delete a Profile referenced by a terminal Issue
- **WHEN** a user attempts to delete a Profile explicitly selected by a terminal Issue
- **THEN** deletion SHALL fail and the error SHALL identify that Issue reference

#### Scenario: Delete an unreferenced custom Profile
- **WHEN** a user deletes a custom Profile with no Project default, Issue, or active WorkflowRun reference
- **THEN** the Profile SHALL be removed from that Project's collection

#### Scenario: Issue selection commits before a concurrent deletion
- **WHEN** an Issue Profile selection commits for a custom Profile before a concurrent deletion can remove that Profile
- **THEN** deletion SHALL fail and identify the newly committed Issue reference as a blocker

#### Scenario: Deletion commits before a concurrent Issue selection
- **WHEN** deletion of an unreferenced custom Profile commits before a concurrent Issue explicit-selection transaction writes its reference
- **THEN** the Issue selection SHALL fail with the retryable `workflow-profile-not-found` conflict and SHALL not create a dangling reference.
