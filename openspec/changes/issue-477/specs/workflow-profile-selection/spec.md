### Requirement: Project default workflow Profile
`mo project workflow set-default <profile>` SHALL set the current Project's default WorkflowProfile. The command MUST accept only a Profile in that Project's workflow collection, including a readable built-in Profile, and SHALL reject a Profile that is absent from the collection. A newly started WorkflowRun for an Issue without an explicit selection SHALL use the Project default selected at run start.

#### Scenario: Set a custom Profile as the Project default
- **WHEN** a user sets an existing custom Profile as the current Project's default and starts an Issue that has no explicit Profile selection
- **THEN** the new WorkflowRun SHALL use that custom Profile

#### Scenario: Set a Profile from another Project as default
- **WHEN** a user attempts to set a Profile ID that exists only in a different Project as the current Project's default
- **THEN** the command SHALL fail because the Profile is not in the current Project's collection

### Requirement: Explicit Issue Profile selection
`mo issue create` and `mo issue edit` SHALL accept `--workflow-profile <profile>` to store an explicit Profile selection for the Issue. An explicit selection MUST refer to a Profile in the Issue's Project collection and SHALL take precedence over that Project's default when the Issue starts a WorkflowRun.

#### Scenario: Create an Issue with an explicit Profile
- **WHEN** a user creates an Issue with `--workflow-profile mohist/github-pr`
- **THEN** the Issue SHALL store `mohist/github-pr` as its explicit Profile selection and its subsequently started WorkflowRun SHALL use that Profile

#### Scenario: Select an unknown Profile for an Issue
- **WHEN** a user creates or edits an Issue with `--workflow-profile` naming a Profile outside the Issue's Project collection
- **THEN** the command SHALL fail and the Issue's existing selection SHALL remain unchanged

### Requirement: Inherited Issue Profile selection
An Issue without an explicit Profile selection SHALL inherit its Project default. `mo issue edit --inherit-workflow-profile` SHALL clear the Issue's explicit selection so that future WorkflowRuns inherit the Project default. The CLI MUST reject a single `mo issue edit` invocation that supplies both `--workflow-profile` and `--inherit-workflow-profile` locally, before issuing a request; it MUST NOT use a sentinel Profile ID to represent inheritance.

#### Scenario: Return an Issue to Project-default inheritance
- **WHEN** an Issue with an explicit Profile selection is edited with `--inherit-workflow-profile`
- **THEN** the explicit selection SHALL be cleared and a subsequently started WorkflowRun SHALL use the Project default

#### Scenario: Supply conflicting Issue selection flags
- **WHEN** a user runs `mo issue edit` with both `--workflow-profile delivery/review` and `--inherit-workflow-profile`
- **THEN** the CLI SHALL fail locally without sending an Issue update request

### Requirement: Selection changes do not rebind active Runs
Changing a Project default or an Issue's explicit-versus-inherited selection SHALL affect only WorkflowRuns started after that change. Such a selection change MUST NOT switch the Profile bound to an active WorkflowRun.

#### Scenario: Change the Project default during an active Run
- **WHEN** an Issue is running with a Profile selected at startup and the Project default changes
- **THEN** the active WorkflowRun SHALL remain bound to its startup Profile ID, while a later WorkflowRun without an explicit Issue selection SHALL use the new default

#### Scenario: Change an Issue selection after a Run starts
- **WHEN** an Issue has an active WorkflowRun and its stored Profile selection is changed
- **THEN** the active WorkflowRun SHALL remain bound to its startup Profile ID
