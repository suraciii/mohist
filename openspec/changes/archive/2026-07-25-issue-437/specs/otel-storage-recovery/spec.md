### Requirement: An oversized existing observation database is recovered by rebuild

At Server startup, if the existing observation database's combined size (`.db` plus `-wal` plus `-shm`) exceeds the safe budget — defined as 100% of the configured storage budget, strictly above the 90% eviction high watermark so that online eviction always gets the first chance to reclaim — the Server SHALL recover the store by rebuilding an empty observation database rather than attempting slow online reclamation on the startup path. The rebuild SHALL stop observation connections to the oversized database before replacing it, SHALL create a fresh schema-initialized observation database, and SHALL treat the prior observation data as discarded. Because observation data is lossy by design, the rebuild SHALL NOT touch, lock, or otherwise affect the core business database, and a rebuild failure SHALL NOT prevent the core Server from becoming reachable.

#### Scenario: An oversized database is detected at startup

- **WHEN** the Server starts with observation enabled and the existing observation database exceeds the safe budget
- **THEN** the Server SHALL rebuild an empty observation database
- **AND** it SHALL stop observation connections to the oversized database before replacing it
- **AND** the prior observation data SHALL be treated as discarded

#### Scenario: A normal-sized database is not rebuilt

- **WHEN** the Server starts and the existing observation database is within the safe budget
- **THEN** the Server SHALL NOT rebuild the database
- **AND** existing observation data SHALL be preserved

#### Scenario: Rebuild does not affect core data or core startup

- **WHEN** a rebuild is performed or fails
- **THEN** the core business database SHALL NOT be touched, locked, or affected
- **AND** a rebuild failure SHALL NOT prevent the core Server from becoming reachable

#### Scenario: A query overlaps the brief rebuild window

- **WHEN** a read-only observation query is in flight during the bounded rebuild window in which the old file is replaced
- **THEN** that query SHALL fail rather than block or corrupt the rebuild
- **AND** the failure SHALL surface through the existing storage-read degradation path
- **AND** the rebuild window SHALL remain bounded so the failure exposure is short

### Requirement: Rebuild is reported with a clear status reason and log

A rebuild SHALL emit a structured log that identifies it as an observation data reset, and SHALL publish a status reason identifying data reset as the latest degradation cause so an operator can tell from `mo otel status` or `/otel/api/status` that observation data was discarded at startup. After the rebuild completes and the first new write commits, the data-reset degradation cause SHALL clear on the next observation, subject to the existing protection window, leaving the store `healthy` only when no unrelated degradation cause remains.

#### Scenario: An operator inspects status after a rebuild

- **WHEN** the Server starts and rebuilds an oversized observation database
- **THEN** a structured log SHALL record the observation data reset
- **AND** `/otel/api/status` and `mo otel status` SHALL expose a status reason identifying the data reset as the latest degradation cause

#### Scenario: The data-reset reason clears after recovery

- **WHEN** the rebuilt store receives a new write and the next observation runs with no other degradation cause active
- **THEN** the data-reset degradation cause SHALL clear
- **AND** status SHALL recover to `healthy` only when no unrelated degradation cause remains

### Requirement: Rebuild does not block core service startup

The rebuild SHALL NOT hold the core Server startup path for an unbounded or long duration. The work required to detect an oversized database and to stop its observation connections SHALL be bounded and SHALL NOT scale with the amount of history in the oversized database (for example, it SHALL NOT scan or iterate every Trace or Span). Creating the fresh empty database SHALL be bounded by schema initialization, not by the size of the file being replaced. Core services SHALL become reachable within their normal startup bound regardless of how large the oversized observation database is.

#### Scenario: Startup cost does not scale with the oversized database

- **WHEN** the Server starts once with a moderately oversized observation database and once with a very large one
- **THEN** the startup work to detect the oversized state and stop observation connections SHALL be bounded equally in both cases
- **AND** neither startup SHALL scan or iterate every Trace or Span in the oversized database

#### Scenario: Core services stay reachable during rebuild

- **WHEN** a rebuild is in progress at startup
- **THEN** the core Server SHALL become reachable within its normal startup bound
- **AND** core services SHALL NOT be held waiting for the oversized observation database to be processed
