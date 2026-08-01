### Requirement: Legacy State converges to canonical at Server startup
WorkflowRun State persisted in a legacy format SHALL be converted to the single canonical format by a data upgrader that runs during database initialization, after EF migrations and before the Server accepts any request. The service phase SHALL read only canonical State. Database initialization SHALL be the sole upgrade entry: schema and SQLite-unambiguous JSON transforms live in EF migrations; Workflow-semantic transforms that require structural comparison or ambiguity rejection SHALL run as an ordered, idempotent C# data upgrader using the one existing conversion rule, and SHALL NOT be duplicated in SQL.

#### Scenario: Database with legacy rows is fully converted before service
- **WHEN** the Server starts against a database whose `WorkflowRuns` table contains rows persisted in legacy formats
- **THEN** the data upgrader SHALL convert every legacy row to canonical State during initialization
- **AND** the Server SHALL NOT enter its service phase until the conversion has committed

#### Scenario: No per-row schema version column is introduced
- **WHEN** State format evolves
- **THEN** the `WorkflowRuns` table SHALL NOT gain a per-row `SchemaVersion` / `StateSchemaVersion` column
- **AND** format convergence SHALL rely solely on startup migration, not on read-path branching per row

### Requirement: No-write preflight validates every candidate
Before writing anything, the data upgrader SHALL perform a preflight over all `WorkflowRuns` rows: identify legacy candidates, produce each converted State using the single conversion rule, and deserialize the result against the current model. The preflight SHALL NOT write to the database.

#### Scenario: Ambiguous legacy row is named and blocks startup with no writes
- **WHEN** a row's legacy State cannot be unambiguously converted (for example, same-definition recovery attempts declare differing handlers or task declarations) or cannot be deserialized after conversion
- **THEN** the data upgrader SHALL fail preflight without writing any State or ETag
- **AND** the failure SHALL name the offending WorkflowRun
- **AND** the Server SHALL be blocked from entering its service phase

#### Scenario: Clean preflight leaves the database untouched
- **WHEN** preflight runs over a database where every row is already canonical
- **THEN** no candidate SHALL be identified, no backup SHALL be created, and no State or ETag SHALL be written

### Requirement: Consistent backup and integrity check before any rewrite
Before rewriting any State, the data upgrader SHALL produce a consistent SQLite backup using online backup or `VACUUM INTO`. Copying only the main `.db` file SHALL NOT be accepted as a backup under WAL mode. The upgrader SHALL verify the backup opens and that `PRAGMA integrity_check` returns `ok` before proceeding.

#### Scenario: Backup is taken and integrity-checked before writes
- **WHEN** preflight identifies one or more legacy candidates
- **THEN** a consistent backup SHALL be created and its `PRAGMA integrity_check` SHALL pass before any State write occurs

#### Scenario: Backup failure prevents all State and ETag writes
- **WHEN** the backup step fails
- **THEN** no candidate State SHALL be written and no ETag SHALL change
- **AND** the original rows SHALL remain byte-for-byte unchanged

#### Scenario: In-memory source is rejected for backup
- **WHEN** the source SQLite database is in-memory
- **THEN** the backup step SHALL reject it without altering the connection's open state

### Requirement: Atomic single-transaction commit increments each migrated ETag once
All converted rows SHALL be written in a single database transaction. For each legacy row actually rewritten, the upgrader SHALL increment that row's ETag by exactly one within the same transaction. The commit SHALL cover every candidate identified by preflight.

#### Scenario: One failing row rolls back the entire batch
- **WHEN** any candidate row fails to write during the commit transaction
- **THEN** the transaction SHALL roll back so that no candidate's State or ETag changes
- **AND** all original rows SHALL remain unchanged

#### Scenario: Many candidates commit together with one ETag bump each
- **WHEN** preflight identifies more candidates than a single fetch batch (over five hundred)
- **THEN** all candidates SHALL still commit in one transaction and each rewritten row SHALL have its ETag incremented exactly once

### Requirement: Canonical rows are byte-stable and migration is idempotent
A row already in canonical format SHALL NOT be rewritten: its persisted State bytes and ETag SHALL remain unchanged. The data upgrader SHALL be idempotent by persisted byte: re-running it after a successful migration SHALL identify zero candidates, write nothing, and change no State or ETag. Each converted State SHALL equal the single converter's output byte-for-byte, so the post-migration direct read yields the same WorkflowRun state as the pre-migration compatibility read.

#### Scenario: Canonical row untouched, legacy row migrated once
- **WHEN** a database contains one canonical row and one legacy row and the upgrader runs
- **THEN** the canonical row's State and ETag SHALL be unchanged
- **AND** the legacy row's State SHALL be rewritten and its ETag incremented exactly once

#### Scenario: Repeat run is a no-op
- **WHEN** the upgrader runs again over a database it has already migrated
- **THEN** it SHALL report zero candidates and zero writes, create no backup, and leave every State and ETag unchanged

### Requirement: Migration does not filter by WorkflowRun lifecycle
Conversion SHALL NOT be scoped to terminal runs. A `failed` run — which remains retry-able / rerun-able and is therefore not a terminal lifecycle state — SHALL be migrated under the same rule as any other run, and its recovery budget, rerun, and retry behavior SHALL be preserved after migration.

#### Scenario: Failed run with exhausted recovery migrates and can rerun
- **WHEN** a `failed` run whose recovery is exhausted is migrated and then loaded and rerun
- **THEN** the migrated State SHALL load against the current model
- **AND** rerun SHALL produce a fresh stage attempt (resume event, stage-started event, attempt incremented, prior tasks cleared) and persist identically on reload
