### Requirement: OTel status has exactly three states

The OTel status API and `mo otel status` SHALL report exactly one state: `off`, `healthy` or `degraded`. `off` SHALL mean observability is disabled and its collector and maintenance work are not running. `healthy` SHALL mean observability is enabled, ingestion, storage and process-resource sampling are usable, and no rejection or data-loss protection is active. `degraded` SHALL mean observability is enabled but ingestion, storage or process-resource sampling is unavailable, a write has failed, or a protection component has reported that telemetry is currently being rejected or dropped. The status read surface SHALL remain available while the Mohist Server is reachable, including when observability is `off`. This change defines the rejection/drop publication and read contract; it SHALL NOT introduce the storage/admission policy that decides to reject data.

#### Scenario: Observability is disabled

- **WHEN** the Server is reachable and observability is disabled
- **THEN** the status API and `mo otel status` SHALL report `off`
- **AND** the status read SHALL NOT require the collector, exporter or storage-maintenance work to be running
- **AND** current working set and GC heap pressure SHALL remain available from the immediate bounded process sample
- **AND** CPU utilization SHALL be `null` during the first-sample warm-up before an elapsed-time delta exists
- **AND** storage usage and growth SHALL be unavailable because the OTel storage probe is not running

#### Scenario: Initial process publication does not wait for storage

- **WHEN** the Server first becomes reachable in either collection state
- **THEN** its current process values or their explicit unavailable result SHALL already have been published
- **AND** a storage probe SHALL begin only after the Server has finished starting when collection is enabled
- **AND** a storage open or metadata failure SHALL NOT prevent the Server or its status read surface from becoming reachable

#### Scenario: Collection and storage are operating normally

- **WHEN** observability is enabled, collection is online, a production ingestion write transaction has committed, current storage metadata and process samples are available, and no telemetry is being rejected or dropped
- **THEN** the status API and `mo otel status` SHALL report `healthy`

#### Scenario: Storage write readiness has not been established

- **WHEN** observability is enabled and its metadata probe succeeds before any production ingestion write transaction has committed
- **THEN** status SHALL remain `degraded` with `storage_unverified`
- **AND** metadata readability SHALL NOT be treated as proof that ingestion can commit
- **AND** an empty, wholly rejected, wholly dropped or malformed request that does not enter the write transaction SHALL NOT clear `storage_unverified`

#### Scenario: Storage cannot be read

- **WHEN** observability is enabled and the OTel database cannot be read
- **THEN** the status API and `mo otel status` SHALL report `degraded`
- **AND** SHALL expose a reason identifying the read failure instead of substituting zero values that appear healthy

#### Scenario: Storage read recovers without clearing another cause

- **WHEN** a storage probe fails and a later storage probe succeeds while another degradation cause remains active
- **THEN** current storage values SHALL be published and only the storage-read cause SHALL clear
- **AND** status SHALL remain `degraded` until the unrelated cause recovers

#### Scenario: Process resources cannot be sampled

- **WHEN** the process-resource reader fails while observability is enabled
- **THEN** the status API and `mo otel status` SHALL report `degraded` with `process_read_failed`
- **AND** the unavailable process values SHALL be `null` rather than stale or fabricated values
- **AND** a later successful sample SHALL clear that source

#### Scenario: Process resources cannot be sampled while off

- **WHEN** the process-resource reader fails while observability is disabled
- **THEN** status SHALL remain `off` and expose `process_read_failed` with unavailable process values
- **AND** it SHALL NOT start the collector or storage maintenance to recover the sample

#### Scenario: A write fails or a protection component reports telemetry loss

- **WHEN** an OTel write fails or an ingestion/storage protection component publishes a rejected or dropped outcome
- **THEN** the status API and `mo otel status` SHALL report `degraded`
- **AND** SHALL expose the latest degradation reason

#### Scenario: Rejection or loss protection expires

- **WHEN** rejection or dropped telemetry activates protection degradation and no later rejection or drop occurs for five minutes of injected time
- **THEN** the protection degradation cause SHALL clear on the next observation, sample or status read
- **AND** status SHALL recover to `healthy` only when no unrelated degradation cause remains

#### Scenario: Repeated rejection extends protection degradation

- **WHEN** another rejection or drop occurs before the five-minute protection interval expires
- **THEN** the protection degradation SHALL remain active for five minutes from that latest outcome
- **AND** the repeated outcome SHALL NOT emit another transition log while status remains `degraded`

#### Scenario: A write recovers without clearing another cause

- **WHEN** an OTel write fails and a later production write succeeds while another degradation cause remains active
- **THEN** only the storage-write cause SHALL clear
- **AND** status SHALL remain `degraded` until the unrelated cause recovers

#### Scenario: Collector bind uses the alternate Server without duplicate storage probing

- **WHEN** the configured collector port cannot bind and the alternate Server starts successfully
- **THEN** collection SHALL remain enabled and status SHALL report the collector-bind degradation
- **AND** the alternate's initial status snapshot SHALL expose `collector_bind_failed` as the latest degradation
- **AND** storage probing SHALL start only from the successfully started Server rather than from both attempted Servers

#### Scenario: A later failure follows collector bind degradation

- **WHEN** an alternate Server starts with `collector_bind_failed` and a process, storage or protection failure occurs later
- **THEN** status SHALL remain degraded with the collector failure still active
- **AND** the later event SHALL replace `collector_bind_failed` as `latest_degradation`

### Requirement: Status exposes storage, ingestion and process pressure

Each status snapshot SHALL expose `status`, `collector_online` and `since`; a `storage` object containing `usage_bytes`, `budget_bytes`, `growth_bytes_per_second` and `growth_window_seconds`; a `telemetry` object containing `received_spans`, `saved_spans`, `rejected_spans` and `dropped_spans`; a `process` object containing `cpu_utilization`, `working_set_bytes` and `gc_heap_bytes`; `latest_degradation`; and `routes`. Sampled values that are not available SHALL be present as JSON `null`, not fabricated zeros. Telemetry counters SHALL cover the current Server process lifetime and count Span attempts: received means parsed, saved means committed including duplicate upserts, rejected means intentionally refused with a non-retryable partial-success response, and dropped means malformed or otherwise lost without requesting a retry. A retryable storage-failure rollback or request-token cancellation after transaction start SHALL count parsed attempts as received but not saved, rejected or dropped; cancellation SHALL leave `storage_write` unchanged and SHALL NOT activate or refresh `ingest_protection`, while ordinary time-based protection expiry still applies. `latest_degradation` SHALL be `null` when no degradation has been recorded and otherwise SHALL contain `code`, `message` and `at`, with bounded text values. `mo otel status` SHALL render the same categories and state without requiring direct access to `otel.db`. The legacy `trace_count` and `span_count` fields MUST NOT be present.

#### Scenario: An operator reads healthy status

- **WHEN** the status API is read while observability is healthy
- **THEN** the response SHALL include storage budget, usage and growth, all four telemetry outcome counts, and current CPU, working set and GC heap pressure
- **AND** `mo otel status` SHALL make the same categories directly visible

#### Scenario: A protection component publishes rejection after startup

- **WHEN** telemetry has been received and an ingestion/storage protection component publishes that part of it was rejected during the current Server process lifetime
- **THEN** the received and rejected values SHALL reflect those outcomes
- **AND** a Server restart SHALL begin new runtime outcome counters rather than reconstructing historical counts with full-table scans

### Requirement: Status exposes a bounded ranked-route summary

Status SHALL expose at most 10 stable-route summaries from the five-minute local window at one-second resolution. A request SHALL NOT leave the window before it is five minutes old and SHALL NOT remain in the window for five minutes plus one second or longer. Each entry SHALL contain `route`, `request_count`, `average_duration_ms`, `max_duration_ms`, `database_calls_per_request` and `downstream_calls_per_request`. Entries SHALL be ordered by combined database-plus-downstream calls per request descending, average latency descending, and stable route name ordinal ascending; neither route identity nor response size SHALL grow with raw URL or request cardinality. This top-ranked view is the issue's "anomalous route" diagnostic and SHALL NOT imply a learned anomaly threshold.

#### Scenario: Recent routes have different amplification

- **WHEN** multiple routes have observations in the current five-minute window with different database calls, downstream calls and latency
- **THEN** status SHALL return no more than 10 stable-route entries ordered by the complete amplification, latency, and route-name key
- **AND** every entry SHALL expose request count, latency, database calls per request and downstream calls per request

#### Scenario: No route observations exist in the retained window

- **WHEN** no route observation falls within the current five-minute window or its permitted one-second boundary bucket
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
