### Requirement: Variables resolve across exactly three scopes
Workflow Variables SHALL resolve across exactly three scopes: Project, Issue, and Run. There SHALL be no fourth initialization-defaults layer below the Run scope. The persisted variable shape SHALL consist of only `vars` and `stages`; the `DefaultVars` and `DefaultStages` fields SHALL NOT exist.

#### Scenario: Three-layer merge produces effective variables
- **WHEN** effective variables are resolved for a Run with Project, Issue, and Run variable layers set
- **THEN** the result SHALL merge Project → Issue → Run with later scopes overriding earlier ones
- **AND** SHALL NOT consult any defaults layer

#### Scenario: Explicit write does not trigger a clear-default protocol
- **WHEN** an explicit variable write targets a key that was previously only available as an implicit default
- **THEN** the write SHALL apply under standard Run-scope precedence
- **AND** SHALL NOT execute any default-key stripping or clearing logic

### Requirement: Run startup must not inject an archive variable
Run startup SHALL NOT seed any `archive` variable key. The effective Run variables immediately after Run creation SHALL NOT contain an `archive` entry unless an explicit Project, Issue, or Run variable declared one.

#### Scenario: Fresh Run has no archive key
- **WHEN** a new WorkflowRun is created and started
- **THEN** the Run's effective variables SHALL NOT contain an `archive` key

### Requirement: Archive action defines explicit absent-hint behavior
The `mohist/archive-change` Action SHALL define and spec-cover its behavior when `archiveHint` is absent. Its optional `archiveHint` input SHALL be engine-sourced from `vars.archive` in the immutable dispatch snapshot, rather than from a profile `with` template. When the variable is absent, Runner SHALL omit the input. The user-visible result when `archiveHint` is absent SHALL match the result before this change: the Action computes a fresh dated archive destination, moves the change directory, and records the destination for subsequent idempotency.

#### Scenario: Archive with no hint computes a fresh destination
- **WHEN** the `mohist/archive-change` Action runs with no `archiveHint` input
- **AND** the source change directory exists and contains files
- **THEN** the Action SHALL compute a fresh archive destination under the archive root using the current date
- **AND** SHALL move the change directory to that destination

#### Scenario: Replay derives the persisted archive destination without a profile default
- **WHEN** a prior archive attempt has written its workspace-relative destination to Run `vars.archive`
- **AND** a retry or rerun receives a new dispatch snapshot with that variable
- **THEN** Runner SHALL inject that value as the `archiveHint` Action input
- **AND** the profile task SHALL NOT need an `archiveHint` entry or `${{ vars.archive }}` reference in `with`
- **AND** when the destination exists and the source directory is gone, the Action SHALL succeed idempotently

#### Scenario: Archive idempotency with a hint is unchanged
- **WHEN** the `mohist/archive-change` Action runs with an `archiveHint` pointing to an existing destination
- **AND** the source change directory no longer exists
- **THEN** the Action SHALL succeed idempotently without moving any directory

### Requirement: Defaults persistence column is removed
The `WorkflowRunProfiles` persistence table SHALL NOT carry a default-variables column. A migration SHALL remove the column; existing default-variable data SHALL NOT be needed for correct operation after migration because no defaults layer exists.

#### Scenario: Database schema has no default variables column
- **WHEN** the migration is applied to the database
- **THEN** the `WorkflowRunProfiles` table SHALL NOT have a column storing default variables
- **AND** variable persistence SHALL use only the explicit variables column
