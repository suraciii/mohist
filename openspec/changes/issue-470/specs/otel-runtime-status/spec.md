### Requirement: OTel status has exactly three states

The OTel status API and `mo otel status` SHALL report exactly one state: `off`, `healthy` or `degraded`. `off` SHALL mean observability is disabled and its collector and maintenance work are not running. `healthy` SHALL mean observability is enabled, ingestion and storage are usable, and no rejection or data-loss protection is active. `degraded` SHALL mean observability is enabled but ingestion or storage is unavailable, a write has failed, or telemetry is currently being rejected or dropped. The status read surface SHALL remain available while the Mohist Server is reachable, including when observability is `off`.

#### Scenario: Observability is disabled

- **WHEN** the Server is reachable and observability is disabled
- **THEN** the status API and `mo otel status` SHALL report `off`
- **AND** the status read SHALL NOT require the collector, exporter or storage-maintenance work to be running

#### Scenario: Collection and storage are operating normally

- **WHEN** observability is enabled, collection and storage are usable, and no telemetry is being rejected or dropped
- **THEN** the status API and `mo otel status` SHALL report `healthy`

#### Scenario: Storage cannot be read

- **WHEN** observability is enabled and the OTel database cannot be read
- **THEN** the status API and `mo otel status` SHALL report `degraded`
- **AND** SHALL expose a reason identifying the read failure instead of substituting zero values that appear healthy

#### Scenario: A write fails or telemetry is lost

- **WHEN** an OTel write fails or telemetry is rejected or dropped
- **THEN** the status API and `mo otel status` SHALL report `degraded`
- **AND** SHALL expose the latest degradation reason

### Requirement: Status exposes storage, ingestion and process pressure

Each status snapshot SHALL expose the observability storage budget, current usage and growth rate with its measurement window; telemetry received, saved, rejected and dropped since the current Server process started; the latest degradation reason when one exists; and current process CPU, working set and GC heap pressure. `mo otel status` SHALL render these values and the state returned by the API without requiring direct access to `otel.db`.

#### Scenario: An operator reads healthy status

- **WHEN** the status API is read while observability is healthy
- **THEN** the response SHALL include storage budget, usage and growth, all four telemetry outcome counts, and current CPU, working set and GC heap pressure
- **AND** `mo otel status` SHALL make the same categories directly visible

#### Scenario: Rejection occurs after startup

- **WHEN** telemetry has been received and part of it has been rejected during the current Server process lifetime
- **THEN** the received and rejected values SHALL reflect those outcomes
- **AND** a Server restart SHALL begin new runtime outcome counters rather than reconstructing historical counts with full-table scans

### Requirement: Status exposes a bounded anomalous-route summary

Status SHALL expose at most 10 stable-route summaries from the most recent five minutes. Each entry SHALL contain the stable route name, request count, latency information, database calls per request and downstream calls per request. Entries SHALL be ordered so routes with greater work amplification and latency are presented first, and neither route identity nor response size SHALL grow with raw URL or request cardinality.

#### Scenario: Recent routes have different amplification

- **WHEN** multiple routes have observations in the current five-minute window with different database calls, downstream calls and latency
- **THEN** status SHALL return no more than 10 stable-route entries ordered by amplification and latency
- **AND** every entry SHALL expose request count, latency, database calls per request and downstream calls per request

#### Scenario: No recent route observations exist

- **WHEN** no route observation falls within the current five-minute window
- **THEN** status SHALL return an empty route summary rather than historical database-derived entries

### Requirement: Status cost is independent of telemetry history

Generating status MUST NOT execute full-table Trace or Span counts and MUST NOT enumerate telemetry history. It SHALL derive runtime counts from bounded in-memory state and storage pressure from bounded storage metadata. The memory retained for status and the serialized response SHALL have fixed upper bounds, and adding unrelated historical traces or spans MUST NOT increase the number of database records inspected, downstream calls made or response entries produced by a status read. Exact historical `trace_count` and `span_count` are not part of the resulting status contract.

#### Scenario: Historical telemetry grows

- **WHEN** the same current status is read once with little historical telemetry and once with a large amount of unrelated historical telemetry
- **THEN** both reads SHALL inspect the same bounded amount of state and produce responses within the same fixed bounds
- **AND** neither read SHALL execute `COUNT(*)` over the Trace or Span tables

#### Scenario: Route and reason cardinality grows

- **WHEN** request volume, route cardinality or repeated degradation events exceed the configured local bounds
- **THEN** retained status memory and serialized response size SHALL remain bounded
- **AND** status SHALL still contain at most 10 route entries and one latest degradation reason

### Requirement: Degradation logs only on state transitions

Mohist SHALL emit one structured status-change log when observability transitions from `healthy` to `degraded` and one when it recovers from `degraded` to `healthy`. Each transition log SHALL identify the previous state, new state and reason. Mohist MUST NOT repeatedly emit the transition log while the state remains unchanged. Time-window and transition behavior SHALL be driven by injectable time.

#### Scenario: Degradation persists

- **WHEN** observability transitions from `healthy` to `degraded` and repeated checks observe the same degradation
- **THEN** Mohist SHALL emit exactly one structured transition log for entering `degraded`
- **AND** the repeated checks SHALL NOT emit duplicate transition logs

#### Scenario: Observability recovers

- **WHEN** observability transitions from `degraded` to `healthy`
- **THEN** Mohist SHALL emit exactly one structured recovery log containing the previous and new state

### Requirement: Observability degradation does not redefine core health

The core `/api/health` endpoint SHALL continue reporting core Server readiness independently of OTel status. A `degraded` observability state alone MUST NOT make `/api/health` return an unavailable response or a non-success status.

#### Scenario: OTel is degraded while the Server remains usable

- **WHEN** the OTel database is unavailable or telemetry is being rejected but the core Server can still serve requests
- **THEN** OTel status SHALL report `degraded`
- **AND** `/api/health` SHALL continue returning its normal successful readiness response
