### Requirement: Runtime signals are available through a standard Meter and a local summary

Mohist SHALL emit runtime observability signals through a standard .NET `Meter` and SHALL update an in-process summary from the same observations. The signals SHALL cover stable-route request count and latency, database-call and downstream-call count, current process CPU, working set and GC heap pressure, telemetry received, saved, rejected and dropped, and observability storage usage and growth. Request duration SHALL be the elapsed time between injected `TimeProvider` monotonic timestamps captured immediately before and after the awaited endpoint delegate. A request observation SHALL close atomically in `finally`, including exceptional and cancelled completion; work that executes later in a detached background context MUST NOT mutate that completed request. If exceptional or cancelled completion has no stable HTTP response status, its normalized status SHALL be `0`. Measurement MUST NOT swallow or replace the endpoint exception. Reading the local summary SHALL NOT depend on an external metrics backend or on exporting the local metrics to the built-in collector.

#### Scenario: A request updates both observation surfaces

- **WHEN** Mohist completes an instrumented request that performs database or downstream calls
- **THEN** the standard `Meter` SHALL record the request, latency, database-call count and downstream-call count
- **AND** the local summary SHALL reflect the same route-level observations without querying an external metrics system

#### Scenario: External metric collection is absent

- **WHEN** no external metrics reader or backend is configured
- **THEN** Mohist SHALL continue maintaining the local runtime summary used by its status surface
- **AND** the absence of an external metrics system SHALL NOT change Workflow or Session behavior

#### Scenario: Detached work outlives its originating response

- **WHEN** a request launches background work that executes after the response-completion boundary
- **THEN** the completed route observation SHALL remain unchanged by that later work
- **AND** the background execution SHALL NOT retain the request's ambient work scope

#### Scenario: A request exits exceptionally

- **WHEN** an instrumented endpoint advances injected time and then exits by exception or cancellation
- **THEN** the route observation SHALL record the injected elapsed duration and close exactly once
- **AND** SHALL use status `0` when no stable response status exists
- **AND** SHALL propagate the original exception or cancellation unchanged
- **AND** verification SHALL advance fake time without wall-clock waits or elapsed-time tolerances

### Requirement: Runtime metric catalog is fixed

Mohist SHALL publish the following instruments through a `Meter` named `Mohist.Server.Runtime`:

| Instrument | Kind | Unit | Attribute keys |
|---|---|---|---|
| `mohist.server.http.request.count` | Counter | `{request}` | `http.route`, `http.request.method`, `http.response.status_code` |
| `mohist.server.http.request.duration` | Histogram | `ms` | `http.route`, `http.request.method`, `http.response.status_code` |
| `mohist.server.http.request.database_calls` | Histogram | `{call}` | `http.route`, `http.request.method`, `http.response.status_code` |
| `mohist.server.http.request.downstream_calls` | Histogram | `{call}` | `http.route`, `http.request.method`, `http.response.status_code` |
| `mohist.server.path.candidates` | Histogram | `{item}` | `mohist.path` |
| `mohist.server.path.processed` | Histogram | `{item}` | `mohist.path` |
| `mohist.server.path.transcript_records` | Histogram | `{record}` | `mohist.path` |
| `mohist.otel.spans.received` | Counter | `{span}` | none |
| `mohist.otel.spans.saved` | Counter | `{span}` | none |
| `mohist.otel.spans.rejected` | Counter | `{span}` | none |
| `mohist.otel.spans.dropped` | Counter | `{span}` | none |
| `mohist.otel.storage.usage` | ObservableGauge | `By` | none |
| `mohist.otel.storage.budget` | ObservableGauge | `By` | none |
| `mohist.otel.storage.growth` | ObservableGauge | `By/s` | none |
| `mohist.process.cpu.utilization` | ObservableGauge | `1` | none |
| `mohist.process.memory.working_set` | ObservableGauge | `By` | none |
| `mohist.process.runtime.dotnet.gc.heap` | ObservableGauge | `By` | none |

The instrument name, kind, unit and complete attribute-key set SHALL be treated as one compatibility contract. Mohist MUST NOT add undeclared attributes to these instruments.

Telemetry outcomes SHALL count Span attempts. `received` SHALL count each Span attempt that is successfully parsed at the ingest boundary. `saved` SHALL count each parsed Span whose write commits, including a duplicate upsert that commits successfully. `rejected` SHALL count each parsed Span intentionally refused by admission or storage protection with a non-retryable OTLP partial-success response. `dropped` SHALL count each malformed or otherwise lost Span for which the response does not request a retry. A parsed Span in a rolled-back write that returns a retryable failure SHALL increment `received`, SHALL NOT increment `saved`, `rejected` or `dropped`, and SHALL activate storage-write degradation. The four counters MUST NOT be required to satisfy a conservation equation.

#### Scenario: The metric catalog changes unintentionally

- **WHEN** an instrument name, kind, unit or attribute-key set differs from the declared catalog
- **THEN** the metric contract verification SHALL fail

#### Scenario: A successful duplicate upsert is saved

- **WHEN** a parsed Span duplicates an existing record and its upsert commits successfully
- **THEN** `received` and `saved` SHALL each increment for that Span attempt
- **AND** `rejected` and `dropped` SHALL NOT increment for that attempt

#### Scenario: Malformed telemetry is dropped without being received

- **WHEN** a Span attempt cannot be parsed and the response does not request a retry
- **THEN** `dropped` SHALL increment for that attempt
- **AND** `received`, `saved` and `rejected` SHALL NOT increment for that attempt

#### Scenario: Protection rejects parsed telemetry

- **WHEN** a protection component intentionally refuses a parsed Span with a non-retryable partial-success response
- **THEN** `received` and `rejected` SHALL each increment for that attempt
- **AND** `saved` and `dropped` SHALL NOT increment for that attempt

#### Scenario: A retryable write rolls back

- **WHEN** a write containing a parsed Span rolls back and the response requests a retry
- **THEN** `received` SHALL increment and storage-write degradation SHALL activate
- **AND** `saved`, `rejected` and `dropped` SHALL NOT increment for that attempt

### Requirement: OTLP HTTP outcomes preserve request encoding

The trace ingestion route SHALL encode every recognized-request response according to the normalized request `Content-Type`, independent of `Accept`. `application/json` SHALL receive JSON and `application/x-protobuf` or `application/protobuf` SHALL receive protobuf with canonical response content type `application/x-protobuf`. Full success SHALL return HTTP 200 with an empty standard `ExportTraceServiceResponse`: `{}` for JSON and the zero-byte default message for protobuf. A non-retryable partial success SHALL return HTTP 200 with `ExportTraceServiceResponse.partial_success`; `rejected_spans` SHALL equal the sum of rejected and dropped Span attempts and `error_message` SHALL be bounded to 256 characters. JSON SHALL use the protobuf-JSON field names `partialSuccess`, `rejectedSpans` as a decimal string, and `errorMessage`; protobuf SHALL use the standard message field numbers.

Whole-body malformed JSON or protobuf SHALL return HTTP 400 with `google.rpc.Status` code 3 (`INVALID_ARGUMENT`) in the recognized request encoding and SHALL NOT publish an ingest outcome. A retryable rolled-back write SHALL return HTTP 503 with `google.rpc.Status` code 14 (`UNAVAILABLE`) in the request encoding. Error messages SHALL be bounded to 256 characters and `details` SHALL be absent. When no supported request encoding can be selected, the route SHALL return HTTP 415 with a JSON `google.rpc.Status`.

#### Scenario: Protobuf ingestion partially succeeds

- **WHEN** a protobuf trace request contains Span attempts that are rejected or dropped without retry
- **THEN** the route SHALL return HTTP 200 and `application/x-protobuf`
- **AND** the body SHALL decode as `ExportTraceServiceResponse` with the combined rejected count and bounded message in `partial_success`

#### Scenario: JSON ingestion fully succeeds

- **WHEN** a JSON trace request is fully accepted and committed
- **THEN** the route SHALL return HTTP 200, `application/json` and the empty JSON message `{}`

#### Scenario: A protobuf body is malformed

- **WHEN** a recognized protobuf request contains a malformed or truncated wire message
- **THEN** the route SHALL return HTTP 400 and `application/x-protobuf`
- **AND** the body SHALL decode as `google.rpc.Status` with code 3 and no details
- **AND** no ingestion outcome SHALL be published for that undecodable body

#### Scenario: A retryable JSON write fails

- **WHEN** a recognized JSON request reaches a write transaction that rolls back with a retryable failure
- **THEN** the route SHALL return HTTP 503 and a JSON `google.rpc.Status` with code 14
- **AND** its parsed Span attempts SHALL retain the retryable accounting semantics above

### Requirement: Metric identity has stable low cardinality

Metric instrument names and the exact label-key set accepted by each instrument SHALL be stable, explicitly test-locked contracts. Route dimensions SHALL use the matched route template or another stable bounded route name, never a concrete request URL. HTTP methods SHALL normalize to `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD`, `OPTIONS` or `OTHER`; response status values SHALL be `100` through `599`, with `0` for an unavailable or invalid status. Agent-path values SHALL be only `agent.status` or `agent.activity`. Project identifiers, issue numbers, WorkflowRun identifiers, AgentSession identifiers, raw URLs, trace identifiers, span identifiers and other per-instance identities MUST NOT appear as metric labels. A request for which no stable route name is available SHALL use the single fallback value `unmatched` rather than its raw path.

#### Scenario: Requests differ only by resource identity

- **WHEN** requests target the same route template with different project, issue, workflow or session values
- **THEN** their runtime metrics SHALL use the same route label value
- **AND** none of those resource identities SHALL appear in any metric label

#### Scenario: An unmatched URL is observed

- **WHEN** a request does not resolve to a stable route template
- **THEN** its metric SHALL use a bounded fallback route identity
- **AND** the raw URL or path SHALL NOT become a label value

### Requirement: Local ranked route diagnostics are bounded and ephemeral

The local runtime summary SHALL retain observations from a five-minute window in 301 rotating one-second buckets and SHALL be bounded independently of request volume, route count and telemetry history. Each bucket SHALL retain at most 256 distinct stable route names plus one `other` aggregate; observations for additional route names in that bucket SHALL fold into `other` without being discarded. An observation SHALL NOT be discarded before it is five minutes old and SHALL NOT be retained for five minutes plus one second or longer. The summary SHALL produce at most 10 route entries ranked without an anomaly threshold: first by `database calls per request + downstream calls per request` descending (one call of either kind has equal weight), then by average latency descending, then by stable route name using ordinal ascending order. Each entry SHALL contain the stable route name, request count, average and maximum latency, database calls per request and downstream calls per request. The retained summary SHALL reset when the Server process restarts and MUST NOT be written to the business database or treated as a Workflow or Session fact.

#### Scenario: More than ten routes are active

- **WHEN** more than 10 stable routes have observations within the current five-minute window
- **THEN** the local diagnostic result SHALL contain no more than 10 route entries
- **AND** its retained memory SHALL remain within a fixed bound

#### Scenario: A bucket exceeds its stable-route bound

- **WHEN** one one-second bucket receives observations for more than 256 distinct stable route names
- **THEN** the bucket SHALL retain at most 256 named-route aggregates and one `other` aggregate
- **AND** every overflow observation SHALL contribute to `other`

#### Scenario: Routes tie on amplification and latency

- **WHEN** two retained routes have equal combined calls per request and equal average latency
- **THEN** the route with the ordinally smaller stable route name SHALL appear first

#### Scenario: Observations age out

- **WHEN** injected time advances beyond five minutes plus the one-second boundary resolution without another observation for a route
- **THEN** the expired observations SHALL no longer contribute to that route's diagnostic values
- **AND** verification SHALL NOT require waiting for wall-clock time

#### Scenario: The Server restarts

- **WHEN** the Server process starts after previously collecting route diagnostics
- **THEN** its local route summary SHALL start empty
- **AND** no prior route summary SHALL be loaded from business persistence

### Requirement: Built-in observability work cannot observe itself into a feedback loop

Requests to OTel ingestion, query and status surfaces, together with OTel storage-maintenance operations, MUST NOT emit the same route, database or downstream signals back into the built-in collector whose work caused them. This exclusion SHALL cover both trace export and runtime metric export to that collector; Mohist MUST NOT send its local runtime metrics to the same built-in OTLP receiver.

#### Scenario: Mohist serves its own OTel endpoints

- **WHEN** the built-in collector receives telemetry or Mohist serves an OTel query or status request
- **THEN** that work SHALL NOT be exported back to the same collector as another equivalent observation
- **AND** repeated processing SHALL terminate without generating a recursive signal chain

#### Scenario: Storage maintenance runs

- **WHEN** observability storage metadata is inspected or storage maintenance executes
- **THEN** its database and downstream operations SHALL NOT feed equivalent signals back into the built-in collector
