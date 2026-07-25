### Requirement: Traces older than the retention age are deleted

The built-in observation store SHALL retain a Trace for at most the configured retention age (default 72 hours) and SHALL delete Traces whose latest activity is older than that age. The retention age SHALL be exposed as a single `Mohist:Otel` configuration value with the spec default of 72 hours and no additional user-tunable retention knobs. A Trace's age SHALL be measured by its `end_time` (the latest Span time), so a Trace still receiving new Spans is not deleted while it is growing. The deletion unit SHALL be a complete Trace: a Trace row and every Span row that shares its `trace_id` SHALL be removed together, and deletion SHALL NOT leave orphan Span rows for a removed Trace or orphan Trace rows for removed Spans. All time comparisons SHALL be driven by an injectable `TimeProvider`, never by a wall clock.

#### Scenario: A trace ages past the retention limit

- **WHEN** a Trace whose `end_time` is older than the retention age exists in the store
- **THEN** the maintenance loop SHALL delete that Trace's row and all of its Span rows
- **AND** no Span row for that `trace_id` SHALL remain after the deletion commits
- **AND** no Trace row SHALL remain whose `end_time` is older than the retention age once maintenance completes a full pass

#### Scenario: A trace still receiving spans is not aged out

- **WHEN** a Trace has Span activity within the retention window even though its earliest Span is older than the retention age
- **THEN** the maintenance loop SHALL NOT delete that Trace
- **AND** deletion SHALL be governed solely by the Trace's `end_time` relative to the injected now

#### Scenario: Retention age is configurable with the spec default

- **WHEN** the Server starts without an explicit retention age
- **THEN** the effective retention age SHALL be 72 hours
- **AND** setting `Mohist:Otel` retention age SHALL change the cutoff used by maintenance without exposing any other retention tuning

#### Scenario: Retention uses injectable time

- **WHEN** the injected clock is advanced past the retention age of an existing Trace without any wall-clock elapsing
- **THEN** the next maintenance pass SHALL delete that Trace
- **AND** the maintenance decision SHALL NOT depend on wall-clock time

### Requirement: Retention deletion runs in bounded batches and is resumable

Retention deletion SHALL run in bounded batches, where each batch deletes a fixed maximum number of complete Traces per maintenance invocation and then yields control. A Trace is a complete deletion unit only when its header row and all of its Span rows are removed in the same transaction. Deletion SHALL be interruptible: if a maintenance invocation is cancelled or otherwise does not finish, the Traces already removed in committed batches SHALL stay removed, and the next invocation SHALL resume from the oldest remaining aged Trace rather than restarting or skipping. The per-invocation work and the number of database statements executed by a maintenance pass SHALL be bounded by the configured batch size and SHALL NOT scale with the total amount of history when the number of aged Traces is large.

#### Scenario: More aged traces exist than one batch can remove

- **WHEN** the number of Traces older than the retention age exceeds the configured batch size
- **THEN** a single maintenance invocation SHALL delete at most the batch size of complete Traces
- **AND** remaining aged Traces SHALL be removed by subsequent maintenance invocations in oldest-first order
- **AND** each removed Trace SHALL have its header row and all Span rows removed together

#### Scenario: Maintenance is interrupted mid-pass

- **WHEN** a maintenance invocation is cancelled after some batches have committed but before the pass is complete
- **THEN** the already-committed Trace deletions SHALL remain deleted
- **AND** the next maintenance invocation SHALL resume deletion from the oldest remaining aged Trace without restarting or duplicating work

#### Scenario: Retention cost does not grow with unrelated history

- **WHEN** the same number of aged Traces is removed once with little unrelated history and once with a large amount of unrelated history
- **THEN** both passes SHALL execute the same bounded number of database statements per batch
- **AND** the maintenance pass SHALL NOT perform a full-table scan that scales with total Trace count

### Requirement: Retention only runs while observation is enabled

Retention deletion SHALL execute only as part of the observation maintenance loop and only while observation is enabled. When observation is disabled, the maintenance loop SHALL NOT run and no Trace or Span SHALL be deleted by retention. Re-enabling observation SHALL resume retention from the current injected time without requiring manual cleanup and without deleting Traces that are still within the retention window.

#### Scenario: Observation is disabled

- **WHEN** observation is disabled and Traces older than the retention age exist on disk
- **THEN** the maintenance loop SHALL NOT run
- **AND** no Trace or Span SHALL be deleted until observation is re-enabled

#### Scenario: Observation is re-enabled after being off

- **WHEN** observation is re-enabled and the injected now still places some existing Traces within the retention window
- **THEN** maintenance SHALL resume using the current injected time as the cutoff
- **AND** Traces still within the retention window SHALL NOT be deleted simply because observation was off
