### Requirement: Hub URL carries the runner id and optional build hash

The runner SHALL construct its hub URL from the server URL with any trailing slash stripped, joined with `/hubs/runner?`, and a query string containing `runnerId=<runnerId>`. When a non-null `buildGitHash` is supplied, the query string SHALL additionally carry `buildGitHash=<hash>`. When `buildGitHash` is null or not supplied, the `buildGitHash` parameter MUST be omitted entirely (not rendered as an empty value).

#### Scenario: Build hash included when provided

- **WHEN** the client is constructed with server url `http://localhost:3456`, runner id `runner-1`, and build hash `<hash>`
- **THEN** the hub URL is `http://localhost:3456/hubs/runner?runnerId=runner-1&buildGitHash=<hash>`

#### Scenario: Build hash omitted when null

- **WHEN** the client is constructed with a null build hash
- **THEN** the hub URL is `http://localhost:3456/hubs/runner?runnerId=runner-1`

#### Scenario: Build hash omitted when not supplied

- **WHEN** the client is constructed without a build hash argument
- **THEN** the hub URL is `http://localhost:3456/hubs/runner?runnerId=runner-1`

#### Scenario: Trailing slash on the server url is stripped

- **WHEN** the server url is `http://localhost:3456/`
- **THEN** the hub URL path is `http://localhost:3456/hubs/runner?...` (no doubled slash)

### Requirement: Automatic reconnect uses the fixed retry interval sequence

The SignalR connection SHALL be built with `withAutomaticReconnect([0, 2000, 5000, 10000, 30000])`. The retry interval sequence MUST be preserved byte-for-byte; this change SHALL NOT alter the intervals, their order, or their count.

#### Scenario: Reconnect intervals are the fixed five-value sequence

- **WHEN** the connection is inspected
- **THEN** the automatic-reconnect policy is configured with the millisecond intervals `0, 2000, 5000, 10000, 30000` and no other values

### Requirement: probeLiveness returns false unless a Ping resolves before timeout or abort

`probeLiveness` SHALL return `false` immediately when the connection state is not `Connected`. When connected it SHALL invoke `Ping` on the hub and return `true` if the invocation resolves before the configured `probeTimeoutMs` elapses or the supplied `AbortSignal` aborts. It SHALL return `false` on timeout, on abort (including an already-aborted signal at call time), and on a rejected `Ping` invocation. The probe MUST be idempotent: once settled it SHALL ignore later timeout, abort, or resolution events.

#### Scenario: Non-connected state returns false without invoking Ping

- **WHEN** `probeLiveness` is called while the connection state is not `Connected`
- **THEN** it returns `false` and does not invoke `Ping`

#### Scenario: Successful Ping returns true

- **WHEN** the connection is `Connected` and the `Ping` invocation resolves
- **THEN** `probeLiveness` returns `true` and the invocation was made with method name `Ping`

#### Scenario: Ping rejection returns false

- **WHEN** the `Ping` invocation rejects
- **THEN** `probeLiveness` returns `false`

#### Scenario: Timeout returns false

- **WHEN** the `Ping` invocation does not resolve before `probeTimeoutMs` elapses
- **THEN** `probeLiveness` returns `false`

#### Scenario: Abort signal returns false

- **WHEN** the supplied `AbortSignal` aborts (including an already-aborted signal at call time) before `Ping` resolves
- **THEN** `probeLiveness` returns `false`

### Requirement: forceReconnect stops then starts and swallows stop failures

When the connection is not `Disconnected`, `forceReconnect` SHALL call `stop` and then `start`; an exception thrown by `stop` MUST be swallowed so the subsequent `start` still runs (a half-open socket may throw on stop, and the start is what re-establishes the real state). When the connection is already `Disconnected`, `forceReconnect` SHALL call `start` directly WITHOUT first calling `stop`. After a successful `start`, `forceReconnect` SHALL notify the reconnected callback. If the supplied `AbortSignal` is aborted after the stop completes but before the start, `forceReconnect` SHALL return without starting.

#### Scenario: Connected connection is stopped then started

- **WHEN** `forceReconnect` is called while the connection is `Connected`
- **THEN** `stop` is called, then `start` is called, and the reconnected callback is notified

#### Scenario: Disconnected connection is started directly

- **WHEN** `forceReconnect` is called while the connection is `Disconnected`
- **THEN** `start` is called and `stop` is NOT called

#### Scenario: Stop failure does not prevent start

- **WHEN** `stop` rejects but the connection was not already `Disconnected`
- **THEN** `forceReconnect` resolves without throwing and `start` is still called

#### Scenario: Abort after stop short-circuits the start

- **WHEN** the abort signal becomes aborted after `stop` completes but before `start`
- **THEN** `forceReconnect` returns without calling `start`

### Requirement: Reconnect callback fires with the new connection id

On a SignalR-initiated `onreconnected` and after a manual `forceReconnect` start, the runner SHALL invoke the registered `onReconnected` callback with the new connection id. When the callback argument is not a non-empty string, the runner SHALL fall back to `connection.connectionId`; if neither yields a non-empty id, the callback SHALL NOT be invoked.

#### Scenario: Auto-reconnect delivers the new connection id

- **WHEN** SignalR fires `onreconnected` with a new connection id string
- **THEN** the `onReconnected` callback is invoked with that string

#### Scenario: Missing callback argument falls back to connection.connectionId

- **WHEN** SignalR fires `onreconnected` without a usable connection id argument but `connection.connectionId` is set
- **THEN** the `onReconnected` callback is invoked with `connection.connectionId`

#### Scenario: Manual forceReconnect notifies the callback

- **WHEN** `forceReconnect` completes a successful `start`
- **THEN** the `onReconnected` callback is invoked with the connection's current connection id

### Requirement: getConnectionId reflects the live transport connection id

`getConnectionId` SHALL return `connection.connectionId` verbatim, including `null` before the connection has been started. This change SHALL NOT alter the return contract.

#### Scenario: Null before start

- **WHEN** `getConnectionId` is called on a freshly constructed client that has not started
- **THEN** it returns `null`

#### Scenario: Assigned after start

- **WHEN** `getConnectionId` is called after a successful `start`
- **THEN** it returns the transport-assigned connection id
