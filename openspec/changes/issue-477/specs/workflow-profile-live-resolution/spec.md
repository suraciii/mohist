### Requirement: WorkflowRun Profile identity binding
When a WorkflowRun starts, it SHALL resolve the Issue's explicit Profile selection or inherited Project default and persist only the selected Profile ID as the Run's Profile binding. A WorkflowRun MUST NOT persist a snapshot or version of that Profile's Definition.

#### Scenario: Start a Run from an inherited Project default
- **WHEN** an Issue with no explicit selection starts a WorkflowRun while its Project default is `delivery/review`
- **THEN** the WorkflowRun SHALL bind to the Profile ID `delivery/review` without storing a Definition snapshot

### Requirement: Live Definition resolution for future Stages
When an active WorkflowRun initializes a Stage that has not yet been initialized, it SHALL resolve that Stage from the current Definition of the Run's bound Profile ID. An edit to the bound Profile's Definition SHALL therefore apply to later uninitialized Stages of that active Run.

#### Scenario: Edit the bound Profile before a later Stage initializes
- **WHEN** an active WorkflowRun is bound to `delivery/review`, that Profile is edited, and the Run later initializes an uninitialized Stage
- **THEN** the Stage SHALL be initialized from the edited current Definition of `delivery/review`

### Requirement: Initialized Stage and attempt immutability
Once a Stage has been initialized, later Profile Definition edits MUST NOT change that Stage's initialized tasks, checks, or lock behavior. A Profile edit MUST NOT alter accepted attempts or historical WorkflowRun results.

#### Scenario: Edit a Profile after a Stage initializes
- **WHEN** a Stage has been initialized for an active WorkflowRun and the bound Profile's Definition is edited
- **THEN** the initialized Stage SHALL retain its existing tasks, checks, and lock behavior

#### Scenario: Edit a Profile after an attempt is accepted
- **WHEN** an attempt has been accepted or a WorkflowRun result has been recorded and the bound Profile's Definition is edited
- **THEN** the accepted attempt and recorded history SHALL remain unchanged

### Requirement: Profile selection changes preserve Run binding
Changing the Issue's selection or its Project default after a WorkflowRun starts MUST NOT change the Run's bound Profile ID or cause it to resolve future Stages from a different Profile. An uninitialized future Stage SHALL be affected only by an edit to the Definition of the same bound Profile.

#### Scenario: Change the Issue to a different Profile during a Run
- **WHEN** an active WorkflowRun is bound to `delivery/review` and the Issue or Project selection changes to `mohist/github-pr`
- **THEN** the active Run SHALL remain bound to `delivery/review` and SHALL resolve its later uninitialized Stages from `delivery/review`'s current Definition
