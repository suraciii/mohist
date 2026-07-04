### Requirement: The status query entry point is a pure read

The status query entry point (the method the polling endpoint calls to report current job progress) SHALL be strictly read-only. It SHALL NOT restart systemd units, SHALL NOT persist job state to the store, and SHALL NOT release file locks. The query path SHALL only read the latest persisted job state (and read runtime facts such as the running git hash and readiness if needed to describe progress) and return a projection of the current state. No observable world-mutating side effect SHALL occur on the query path.

#### Scenario: Query does not run any process commands

- **WHEN** the query entry point is invoked for a job that is `running` or `waiting-for-reconnect`
- **THEN** no `systemctl` (or any other) command SHALL be dispatched through `ISystemUpdateCommandRunner`
- **AND** the command runner SHALL receive zero requests attributable to the query call

#### Scenario: Query does not persist job state

- **WHEN** the query entry point is invoked for an active job
- **THEN** `ISystemUpdateStore.SaveAsync` and `SaveIfCurrentAsync` SHALL NOT be invoked as a result of the query call
- **AND** the persisted job state file SHALL be unchanged after the query returns

#### Scenario: Query does not release the file lock

- **WHEN** the query entry point is invoked for a job that currently holds the update lock
- **THEN** `ISystemUpdateStore.ReleaseLockAsync` SHALL NOT be invoked as a result of the query call
- **AND** a subsequent start attempt SHALL still be rejected because the lock remains held

### Requirement: State-machine advancements occur only on an explicit command path

Every state-machine advancement that readiness polling used to trigger as a hidden side effect of the query SHALL move to an explicit command method. The advancements that MUST move are: superseding a stale `waiting-for-reconnect` job whose runtime hash has advanced past the job's source HEAD; persisting a `waiting-for-reconnect` transition when readiness checks fail; and, on readiness success plus running-hash/source-HEAD match, persisting the ready transition, restarting the runner, marking the job `succeeded`, and releasing the lock. The same caller that advances the job today (the polling path) SHALL still advance it after this change — it SHALL do so by invoking the command method, never by relying on the query to mutate state.

#### Scenario: Stale waiting-for-reconnect job is superseded via command

- **WHEN** a `waiting-for-reconnect` job whose source HEAD no longer matches the running git hash is polled
- **THEN** the advancement to `superseded` SHALL be driven by a command-method invocation, not by the query method
- **AND** the resulting state SHALL be persisted and the lock released via the command path

#### Scenario: Waiting-for-reconnect transition is persisted via command

- **WHEN** readiness checks fail and the persisted stage or reason would change
- **THEN** the `waiting-for-reconnect` state with the new stage/reason and a log entry SHALL be persisted by a command-method invocation, not by the query method

#### Scenario: Ready transition, runner restart, and success are driven by command

- **WHEN** readiness succeeds and the running git hash matches the job's source HEAD
- **THEN** the ready transition SHALL be persisted via a command method
- **AND** the runner restart (`systemctl --user restart <runner unit>`) SHALL be dispatched via the command path
- **AND** the job SHALL be advanced to `succeeded` and the lock released via the command path

### Requirement: Transition semantics and ordering are preserved

Moving the advancements off the query path and onto a command path SHALL preserve the existing transition semantics exactly. The set of statuses written, the log-entry stages and messages, the points at which the lock is released, the log-bounding cap, and the ordering of "persist ready → restart runner → persist succeeded → release lock" SHALL remain identical to before this change. This is a behavior-preserving relocation, not a semantic rewrite.

#### Scenario: Success path ordering is unchanged

- **WHEN** readiness and hash match for a job with a runner unit
- **THEN** the persisted ready state SHALL be written before the runner restart is dispatched
- **AND** the runner restart SHALL occur before the job is marked `succeeded`
- **AND** the lock SHALL be released after the `succeeded` state is persisted

#### Scenario: Log content for supersession is preserved

- **WHEN** a stale waiting-for-reconnect job is superseded
- **THEN** the persisted log SHALL contain a `Superseded` entry whose message references both the running git hash and the job's source HEAD
- **AND** the `RunningGitHash` field SHALL reflect the advanced runtime hash

#### Scenario: Ready log content is preserved

- **WHEN** readiness succeeds and the hash matches
- **THEN** the persisted log SHALL contain a `Ready` entry whose message references the readiness result's root asset path
- **AND** the reason SHALL state that the runtime matches the source HEAD and readiness checks passed

#### Scenario: Readiness-failure persistence respects deduplication

- **WHEN** readiness fails with the same stage and reason as already persisted
- **THEN** no new `waiting-for-reconnect` log entry SHALL be appended
- **AND** no state SHALL be persisted for that poll

#### Scenario: Empty running hash never triggers supersession

- **WHEN** a waiting-for-reconnect job is polled while the running git hash is empty or null
- **THEN** the job SHALL NOT be superseded
- **AND** it SHALL remain in its waiting-for-reconnect status

### Requirement: The polling endpoint still reflects job progress

The HTTP polling endpoint SHALL continue to report job progress to callers. Because the advancements now run on the command path that the polling flow invokes, a caller polling repeatedly SHALL observe the job progressing through the same sequence of statuses (waiting-for-reconnect → succeeded, or → superseded) as before this change.

#### Scenario: Repeated polling observes progression to success

- **WHEN** a caller polls the status endpoint while readiness transitions from failing to passing with a matching hash
- **THEN** the observed responses SHALL transition through `waiting-for-reconnect` and then to `succeeded` with stage `Ready`
- **AND** the final response SHALL reflect the released lock (a new update start SHALL be accepted)

#### Scenario: Repeated polling observes progression to superseded

- **WHEN** a caller polls while the runtime hash has advanced past the job's source HEAD
- **THEN** the observed response SHALL transition to `superseded` with stage `Superseded`
- **AND** a subsequent update start SHALL be accepted because the lock was released
