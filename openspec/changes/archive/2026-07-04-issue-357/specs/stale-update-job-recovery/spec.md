### Requirement: A startup reconciler recovers active jobs interrupted by a process restart

The server SHALL run a one-shot reconciler at startup (registered as a hosted service whose start callback runs once before the server begins accepting update work) that reads the latest persisted system-update job and decides whether the active on-disk state was orphaned by a process restart. A job whose `Status` is `running` or `waiting-for-reconnect` AND whose `UpdatedAt` strictly precedes the current process start time SHALL be transitioned to `failed` with a reason that records the restart interruption (for example "interrupted by process restart"). The transition SHALL persist the new `failed` state through `ISystemUpdateStore` and SHALL release the lock held by that job, so the update subsystem no longer reports an update in progress that nothing is actually executing. The reconciler SHALL depend only on `ISystemUpdateStore`, `TimeProvider`, the process-start-time abstraction, and a logger, and SHALL NOT depend on `SystemUpdateService` so the in-flight update path stays decoupled.

#### Scenario: A stale `running` job is marked failed and its lock is released

- **WHEN** the server starts and the latest persisted job has `Status = running` and `UpdatedAt` strictly earlier than the current process start time
- **THEN** the reconciler SHALL persist that job with `Status = failed`
- **AND** the persisted `Reason` SHALL indicate the job was interrupted by a process restart
- **AND** the reconciler SHALL release the lock for that job's `JobId`

#### Scenario: A stale `waiting-for-reconnect` job is marked failed and its lock is released

- **WHEN** the server starts and the latest persisted job has `Status = waiting-for-reconnect` and `UpdatedAt` strictly earlier than the current process start time
- **THEN** the reconciler SHALL persist that job with `Status = failed`
- **AND** the persisted `Reason` SHALL indicate the job was interrupted by a process restart
- **AND** the reconciler SHALL release the lock for that job's `JobId`

#### Scenario: No persisted job leaves the reconciler as a no-op

- **WHEN** the server starts and `ISystemUpdateStore.GetLatestAsync` returns no job
- **THEN** the reconciler SHALL persist nothing and SHALL release no lock

### Requirement: Recovery actually frees the on-disk lock so a new update can start

Because `FileSystemSystemUpdateStore.ReleaseLockAsync` only deletes the `.lock` file when its in-memory `_lockOwnerJobId` equals the supplied job id — and `_lockOwnerJobId` is process-local, so it is `null` in a freshly started process — a plain `ReleaseLockAsync(staleJobId)` after a restart SHALL NOT free the stale lock, leaving the next `TryAcquireLockAsync` blocked forever by `FileMode.CreateNew` failing on the existing lock file. The store SHALL therefore provide a path to release a lock whose owning job is no longer active in the current process, so that the reconciled-to-`failed` job's on-disk `.lock` file is removed. After recovery runs, a subsequent `SystemUpdateService.StartAsync` SHALL be able to acquire the lock and begin a new update rather than receiving `update_in_progress`.

#### Scenario: A stale lock file is removed after recovery

- **WHEN** the reconciler recovers a stale active job whose lock file still exists on disk after the restart
- **THEN** the on-disk `.lock` file for that job SHALL be deleted
- **AND** a new `TryAcquireLockAsync` invoked after recovery SHALL succeed

#### Scenario: A new update can start after recovery

- **WHEN** the reconciler has transitioned the stale job to `failed` and released its lock
- **AND** `SystemUpdateService.StartAsync` is then called with a valid update request
- **THEN** `StartAsync` SHALL acquire the lock (returning `Started = true`)
- **AND** SHALL NOT return the `update_in_progress` error code

### Requirement: Fresh active jobs and terminal jobs are never modified

The reconciler SHALL use `UpdatedAt < process start time` as the stale signal, not merely the presence of an active status. A job whose `UpdatedAt` is greater than or equal to the current process start time is considered fresh (it was written by this process or a concurrent writer) and SHALL be left in place. A job whose `Status` is terminal (`succeeded`, `failed`, `recovered`, `superseded`, or `cancelled`) SHALL never be transitioned by the reconciler, regardless of its `UpdatedAt`, because terminal state is already self-consistent and its lock is no longer relevant to a running update.

#### Scenario: A fresh active job is preserved

- **WHEN** the server starts and the latest persisted job has `Status` of `running` or `waiting-for-reconnect` and `UpdatedAt` greater than or equal to the current process start time
- **THEN** the reconciler SHALL NOT persist any change to that job
- **AND** the reconciler SHALL NOT release its lock

#### Scenario: A terminal job is never touched

- **WHEN** the server starts and the latest persisted job has a terminal `Status` (`succeeded`, `failed`, `recovered`, `superseded`, or `cancelled`)
- **THEN** the reconciler SHALL NOT persist any change to that job
- **AND** the reconciler SHALL NOT release any lock on its behalf

### Requirement: The process start time is sourced through an injectable abstraction

The "current process start time" used as the stale threshold SHALL be obtained through an injectable abstraction registered in the service container, not by reading wall-clock process information directly inside the reconciler. The default production implementation MAY read the actual process start time, but the abstraction SHALL be overridable so that tests drive the reconciler with fake time and a fake process start time. The reconciler SHALL NOT read `DateTimeOffset.UtcNow` or `Environment.TickCount`-style process info directly; all time comparisons SHALL go through the injected `TimeProvider` and process-start-time abstraction, consistent with the `TimeProvider` injection landed in #356.

#### Scenario: Tests drive recovery with fake time and an injected process start time

- **WHEN** a test constructs the reconciler with a fake `TimeProvider` and an injected process start time
- **AND** seeds the store with a stale active job whose `UpdatedAt` precedes that injected start time
- **THEN** the reconciler SHALL transition the job to `failed` and release its lock
- **AND** the test SHALL observe the transition without waiting on real wall-clock time

#### Scenario: Production registers a default process-start-time implementation

- **WHEN** the server is composed via `MohistServiceRegistration.ConfigureMohistServices`
- **THEN** the process-start-time abstraction SHALL be registered with a default implementation
- **AND** the reconciler SHALL be registered as a hosted service so its start callback runs once at server startup
