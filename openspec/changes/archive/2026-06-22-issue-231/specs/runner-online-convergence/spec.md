## ADDED Requirements

### Requirement: Heartbeat carries the runner's current dispatch connection id

The runner's periodic heartbeat request SHALL include the runner's current real-time dispatch connection id as an optional field. The field SHALL reflect the connection id of the SignalR dispatch connection currently in use by the runner client at the moment the heartbeat is sent. The field SHALL be optional; runners that predate this capability MAY omit it without breaking the server.

#### Scenario: Heartbeat includes the current connection id

- **WHEN** the runner client sends a periodic heartbeat while its dispatch connection is established
- **THEN** the heartbeat request SHALL carry the current dispatch connection id in the optional `connectionId` field

#### Scenario: Heartbeat from a legacy runner without the field

- **WHEN** a runner that predates this capability sends a heartbeat without the `connectionId` field
- **THEN** the server SHALL accept the heartbeat
- **AND** the server SHALL NOT alter the runner connection map on account of the missing field
- **AND** the existing liveness (heartbeat timestamp) behavior SHALL be unchanged

### Requirement: Heartbeat refreshes the runner connection map

The server heartbeat endpoint SHALL write or update the `runnerId → connectionId` map from the connection id carried by every heartbeat that includes one. After a successful heartbeat that carries a connection id, the map entry for that runner SHALL reflect the connection id reported by the runner. This makes the periodic heartbeat a second source of truth for the map, alongside the SignalR connect/disconnect events, so that any divergence between the two channels converges to the runner-reported value within one heartbeat cycle.

#### Scenario: Heartbeat repopulates the map after a transient disconnect

- **WHEN** the SignalR dispatch connection has been observed as disconnected on the server side (erasing the map entry)
- **AND** the runner process is still running and its dispatch connection is in fact still usable, or has re-established, and reports a connection id
- **AND** the runner's next heartbeat reaches the server carrying that connection id
- **THEN** the server SHALL write the reported connection id into the map
- **AND** the runner's projected `connectionState` SHALL recover to `connected` within one heartbeat cycle of the heartbeat being received

#### Scenario: Workspace materialization does not return runner_unavailable for transient dispatch-connection issues

- **WHEN** a workflow attempts to materialize a workspace on a runner
- **AND** that runner's process is running, its heartbeat is fresh, and its most recent heartbeat carried a connection id
- **THEN** the materialization SHALL resolve the connection id from the map and dispatch to the runner
- **AND** SHALL NOT fail with `runner_unavailable` solely because of a transient SignalR disconnect/reconnect that the heartbeat has already converged

### Requirement: Erase of the runner connection map is exclusive to the SignalR disconnect path

The heartbeat path SHALL NOT remove a runner's connection map entry. Only the SignalR `OnDisconnectedAsync` path SHALL unregister a runner from the connection map. A heartbeat that omits the `connectionId` field, or that carries a null or empty value, SHALL leave any existing map entry untouched. A heartbeat that carries a connection id SHALL only set or overwrite the entry. This invariant prevents the heartbeat channel and the connection-event channel from fighting over clearing state.

#### Scenario: Heartbeat without connectionId does not clear the map

- **WHEN** the server receives a heartbeat that does not carry a `connectionId` (or carries a null/empty value)
- **AND** a map entry for that runner already exists
- **THEN** the server SHALL NOT remove the existing map entry
- **AND** the entry SHALL remain available for subsequent dispatch resolution

#### Scenario: Heartbeat with a connection id only writes or updates

- **WHEN** the server receives a heartbeat carrying connection id `C` for runner `R`
- **THEN** the server SHALL set the map entry for `R` to `C`, overwriting any previous value
- **AND** the server SHALL NOT call the unregister path on account of this heartbeat

### Requirement: Runner client self-checks dispatch connection liveness and reconnects proactively

The runner client SHALL periodically probe the liveness of its real-time dispatch connection with one lightweight round-trip. When the probe fails, the runner client SHALL initiate a reconnect within one self-check period, rather than waiting for the next server-side dispatch attempt to surface the dead connection. The self-check SHALL NOT alter the heartbeat interval or the liveness timeout threshold, and SHALL NOT depend on a server-side dispatch happening to occur.

#### Scenario: Probe failure triggers proactive reconnect

- **WHEN** a self-check probe of the dispatch connection fails
- **THEN** the runner client SHALL initiate a reconnect attempt within one self-check period
- **AND** SHALL NOT wait for a subsequent server-side dispatch to surface the failure

#### Scenario: Probe success leaves the connection alone

- **WHEN** a self-check probe succeeds
- **THEN** the runner client SHALL NOT initiate a reconnect
- **AND** normal heartbeat and dispatch operation SHALL continue

### Requirement: Runner re-sends a heartbeat immediately after a successful reconnect

On a successful re-establishment of the dispatch connection, the runner client SHALL immediately send one heartbeat carrying the new connection id, without waiting for the next heartbeat cycle tick. This converges the server-side map to the new connection id within seconds of the reconnect.

#### Scenario: Reconnect triggers an immediate heartbeat

- **WHEN** the runner client's dispatch connection is re-established after a probe-induced reconnect
- **THEN** the runner client SHALL send a heartbeat carrying the new connection id before the next heartbeat cycle tick
- **AND** the server SHALL refresh the runner connection map from that heartbeat

### Requirement: Genuine runner loss remains unavailable

The convergence mechanism SHALL NOT weaken detection of an actually-dead runner. A runner whose heartbeat has stopped beyond the configured liveness timeout SHALL remain unavailable, and the existing `orphaned-task-recovery` behavior SHALL continue to apply unchanged. Convergence only repairs the runner-online-but-misjudged case; it SHALL NOT resurrect a runner that is no longer heartbeating.

#### Scenario: Heartbeat timeout still judged unavailable

- **WHEN** a runner process has crashed or gone offline and its heartbeat has not been received within the configured liveness timeout
- **THEN** the runner SHALL be judged unavailable per the existing status derivation
- **AND** the `orphaned-task-recovery` requirements SHALL apply to any of its tracked work

#### Scenario: Convergence does not resurrect a dead runner

- **WHEN** no heartbeat carrying a connection id has been received from a runner within the liveness timeout
- **THEN** no map convergence SHALL mark that runner as connected
- **AND** the runner SHALL remain unavailable
