### Requirement: WorkflowRun Profile identity binding
When a WorkflowRun starts, `WorkflowProfileReferenceCoordinator` SHALL resolve the Issue's explicit Profile selection or inherited Project default and persist only the selected Profile ID as the Run's Profile binding. A WorkflowRun MUST NOT persist a snapshot or version of that Profile's Definition.

#### Scenario: Start a Run from an inherited Project default
- **WHEN** an Issue with no explicit selection starts a WorkflowRun while its Project default is `delivery/review`
- **THEN** the WorkflowRun SHALL bind to the Profile ID `delivery/review` without storing a Definition snapshot

### Requirement: Live Definition resolution for future Stages
When a WorkflowRun starts, it SHALL create its complete Stage lifecycle from the selected Profile Definition's Stage names and declaration order. That Stage topology SHALL remain fixed for the Run. When an active WorkflowRun initializes one of those Stages that has not yet been initialized, it SHALL resolve that Stage from the current Definition of the Run's bound Profile ID. An edit to the bound Profile's Definition SHALL therefore apply to later uninitialized Stages of that active Run without changing their membership or order.

#### Scenario: Edit the bound Profile before a later Stage initializes
- **WHEN** an active WorkflowRun is bound to `delivery/review`, that Profile is edited, and the Run later initializes an uninitialized Stage
- **THEN** the Stage SHALL be initialized from the edited current Definition of `delivery/review`

#### Scenario: Add a Stage after a Run starts
- **WHEN** an active WorkflowRun started with the ordered Stages `plan`, `implement`, and its bound Profile is edited to add `review`
- **THEN** the active Run SHALL continue with only `plan` then `implement`, and SHALL not schedule `review`

#### Scenario: Reorder Stages after a Run starts
- **WHEN** an active WorkflowRun started with the ordered Stages `plan`, `implement`, and its bound Profile is edited to order them `implement`, `plan`
- **THEN** the active Run SHALL retain the startup order `plan` then `implement`

#### Scenario: Remove a future Stage after a Run starts
- **WHEN** an active WorkflowRun started with the ordered Stages `plan`, `implement`, `review`, `plan` has initialized, and its bound Profile is edited to remove `implement`
- **THEN** initialization of the existing future `implement` Stage SHALL fail through the visible WorkflowRun failure path without falling back to an earlier Definition or another Profile

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

### Requirement: Terminal Run releases its deletable custom-Profile backing key
When a WorkflowRun becomes terminal, its WorkflowRun transaction SHALL clear only the nullable custom-Profile backing key used by the restrictive foreign key. It SHALL retain its public Profile ID, initialized Stages, attempts, and history. Existing terminal Runs migrated to this model SHALL likewise retain their public Profile ID with a null backing key.

#### Scenario: Terminalize a Run bound to a custom Profile
- **WHEN** a WorkflowRun bound to a custom Profile reaches a terminal state
- **THEN** its backing key SHALL be cleared in that terminalization transaction while its public Profile ID and history remain unchanged

#### Scenario: Migrate an existing terminal Run
- **WHEN** an existing terminal WorkflowRun is migrated with a resolved custom Profile ID
- **THEN** the Run SHALL retain that public Profile ID and have no custom-Profile backing key
