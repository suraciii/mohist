### Requirement: Incremental appends are non-destructive

The server's task-log store SHALL accept incremental batch appends during task execution without deleting previously-appended entries for the same work item. The Phase 1 append path — which removes all existing entries for a work item before inserting — SHALL NOT be used for incremental appends, because under incremental flushing it would destroy lines already persisted. Appending a batch SHALL add only the entries that batch carries.

#### Scenario: A later incremental append keeps the earlier append's lines

- **WHEN** the store appends an incremental batch for a work item and later appends a second incremental batch for the same work item
- **THEN** the lines from the first batch SHALL remain present
- **AND** the store SHALL hold the union of both batches ordered by seq

#### Scenario: The Phase 1 delete-then-insert is not applied to incremental appends

- **WHEN** an incremental batch is appended for a work item that already has persisted lines
- **THEN** the store SHALL NOT remove the previously-persisted lines
- **AND** only the new batch's lines SHALL be inserted

### Requirement: The terminal reconciliation batch is merged by seq dedup

When the terminal reconciliation batch arrives carrying `seq` values already present from incremental appends, the store SHALL reconcile by `seq` rather than producing duplicates or failing on the unique `(OwnerKind, OwnerId, WorkId, Seq)` index. A `seq` already stored SHALL not be duplicated; a `seq` not yet stored SHALL be inserted. The terminal batch's `truncated` flag SHALL reconcile the work item's truncation status to its authoritative value.

#### Scenario: Overlapping seqs between an increment and the terminal batch are deduped

- **WHEN** an incremental append stored seq 1–10 and the terminal batch also supplies seq 1–10 plus seq 11–15
- **THEN** the store SHALL end with exactly one row per seq (1–15)
- **AND** no unique-index violation SHALL occur

#### Scenario: Lines lost by a failed incremental upload are restored by the terminal batch

- **WHEN** an incremental append's lines never reached the store and the terminal batch later supplies them
- **THEN** those lines SHALL be present in the authoritative store after the terminal append
- **AND** the authoritative store SHALL match the complete non-discarded log

### Requirement: Every received batch is persisted before any real-time fan-out

For each received task-log batch, the server SHALL persist the batch to the authoritative store BEFORE performing any real-time distribution. A failure in real-time distribution SHALL NOT affect the persisted log's completeness, because the persisted log is the authoritative source and the real-time rail is best-effort. The persist-then-fan-out ordering SHALL hold for both incremental and terminal batches.

#### Scenario: Persistence succeeds even when real-time distribution throws

- **WHEN** a batch is received and the subsequent real-time fan-out throws
- **THEN** the batch SHALL already be persisted to the authoritative store
- **AND** the store's completeness SHALL be unaffected by the fan-out failure

#### Scenario: Persistence completes before fan-out is attempted

- **WHEN** the server handles an incoming task-log batch
- **THEN** the store append SHALL complete first
- **AND** real-time fan-out SHALL only run after persistence has succeeded

### Requirement: The Phase 1 upload endpoint and issue-path query contracts are preserved

The task-log upload endpoint shape (`POST /api/{workflow-runs|agent-jobs}/{ownerId}/work/{workId}/task-log`) and the issue-path cursor query (`GET /api/projects/{projectId}/issues/{number}/workflow/tasks/{taskId}/logs` returning `{ lines, nextCursor, truncated }`) SHALL remain unchanged. Incremental and terminal appends SHALL flow through the same endpoint and contract. A task with no log SHALL still return an empty result, never an error. Task-log persistence SHALL remain independent of workflow status adjudication — no upload SHALL pass through any workflow grain or influence task success/failure.

#### Scenario: The issue-path query returns the reconciled complete log after terminal append

- **WHEN** a client queries a task's logs after the terminal reconciliation batch has been appended
- **THEN** the response SHALL return the complete set of non-discarded lines in ascending seq order
- **AND** pagination and the truncation flag SHALL behave as in Phase 1

#### Scenario: The upload endpoint accepts both incremental and terminal batches

- **WHEN** the runner uploads an incremental batch and later a terminal batch through the same endpoint
- **THEN** both SHALL be accepted by the same contract
- **AND** the store SHALL reconcile them by seq
