### Requirement: Run-scoped Variables persistence entry named for what it does

The persistence entry point for Run-scoped Variables MUST be named with the `Store` suffix that `design/conventions.md` reserves for "persistence boundary for one shape." The current `WorkflowRunProfileManager` (which reads and writes Variables, never a Profile) SHALL be renamed to `WorkflowRunVariablesStore`. The rename MUST be repo-wide: no production or test reference to the old name `WorkflowRunProfileManager` SHALL remain.

#### Scenario: The persistence type carries the Store suffix

- **WHEN** the run-scoped Variables persistence class is located
- **THEN** its name MUST be `WorkflowRunVariablesStore`
- **AND** it MUST NOT contain the word `Profile`

#### Scenario: No stale name reference survives

- **WHEN** the entire repository is searched for `WorkflowRunProfileManager`
- **THEN** zero matches MUST appear across production source, tests, registrations, and configuration

### Requirement: Method names drop the Profile wording

The public and internal methods on the renamed store MUST describe their action on Variables, not on a Profile. Method names carrying `Profile` wording solely because of the old class name SHALL be renamed to drop it. The method **behavior**, signatures, and the persisted JSON shape of `VariableBundle` MUST remain unchanged.

#### Scenario: Variable-access methods do not mention Profile

- **WHEN** the renamed store's method names are inspected
- **THEN** no method name SHALL contain `Profile`
- **AND** the get / set / patch / default-seed operations MUST remain accessible under Profile-free names

#### Scenario: Persisted VariableBundle shape is unchanged

- **WHEN** a `VariableBundle` is read, set, patched, or seeded with the default archive key
- **THEN** the serialized form, the ETag concurrency behavior, and the explicit-vs-default precedence rules MUST be identical to before the rename

### Requirement: Backing table name either corrected or documented as a deliberate keep

The backing table and row (`WorkflowRunProfiles` / `WorkflowRunProfileRow`, which actually store Variables) MUST be either (a) renamed to reflect that they hold Variables via an EF Core migration, or (b) explicitly recorded in `design/` as a deliberately-retained misnomer together with the reason a rename was judged not worth it. A silent misnomer — table stays wrongly named with no recorded decision — MUST NOT be the outcome.

#### Scenario: Table renamed with a migration

- **WHEN** option (a) is chosen
- **THEN** an EF Core migration MUST rename the table and row type to a Variables-reflecting name
- **AND** existing rows MUST be preserved across the migration

#### Scenario: Table kept under a documented decision

- **WHEN** option (b) is chosen
- **THEN** a `design/` note MUST state that `WorkflowRunProfiles` / `WorkflowRunProfileRow` is a deliberately-retained misnomer, name the reason a rename was not worth the migration cost, and reference this decision from the row type
- **AND** the note MUST NOT be absent, leaving the misnomer unexplained

#### Scenario: No silent misnomer

- **WHEN** the repository is inspected for how the Variables backing table got its name
- **THEN** either the table name MUST reflect Variables, or a recorded design decision explaining the keep MUST exist

### Requirement: No pass-through wrapper in variable resolution

`WorkflowProfileManager.ResolveLayeredVariablesAsync` (which only delegates to `ResolveConfiguredVariablesAsync` and returns its result unchanged) SHALL be removed. Its single production call site MUST call `ResolveConfiguredVariablesAsync` directly, and every test call site MUST be switched to the same direct call. The resolved `VariableBundle` shape and the variable-resolution behavior MUST be identical to before the wrapper's removal.

#### Scenario: The pass-through wrapper is gone

- **WHEN** the repository is searched for `ResolveLayeredVariablesAsync`
- **THEN** zero matches MUST appear in production source and tests
- **AND** the former production call site MUST invoke `ResolveConfiguredVariablesAsync` directly

#### Scenario: Variable resolution is unchanged after inlining

- **WHEN** effective variables are resolved for a run and stage
- **THEN** the resulting `VariableBundle` (vars, stage vars, defaults, default stage vars) MUST be identical to what the pass-through wrapper produced before its removal
