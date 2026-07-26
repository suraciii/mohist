### Requirement: OTLP request admission and payload size are bounded before buffering

The OTLP trace receiver SHALL admit no more than four requests at once, and an admitted request SHALL hold one admission slot until its ingestion attempt completes or is cancelled. Admission SHALL occur before the receiver reads the request body. A recognized request that cannot obtain a slot SHALL return HTTP `429` with `Retry-After: 1` and SHALL NOT read or retain its body. The receiver SHALL limit the decoded request body to 16 MiB before fully buffering it; the limit SHALL apply after request decompression for JSON and protobuf requests. A request that exceeds this limit SHALL return HTTP `413` and SHALL NOT be parsed, persisted, or published as an ingest outcome.

#### Scenario: Concurrent admission is full before a body is read
- **WHEN** four OTLP trace requests have been admitted and have not completed
- **THEN** a fifth recognized OTLP trace request SHALL receive HTTP `429` with `Retry-After: 1` before the receiver reads its body
- **AND** the receiver SHALL retain no deferred work item or payload for that rejected request

#### Scenario: A decoded request exceeds the size limit
- **WHEN** a JSON or protobuf OTLP trace request exceeds 16 MiB after decoding its request content
- **THEN** the receiver SHALL stop reading before fully buffering the request and return HTTP `413`
- **AND** the request SHALL create no stored Span, Trace summary, or telemetry outcome

### Requirement: OTLP persistence uses serialized bounded write blocks

At most one admitted OTLP request SHALL execute database writes at a time. The receiver SHALL partition accepted Span data into independently committed write blocks containing no more than 4 MiB or 512 Spans, whichever limit is reached first. The receiver MUST NOT place accepted data in a durable or in-memory queue after sending a successful response.

#### Scenario: An accepted request crosses a write-block boundary
- **WHEN** an admitted OTLP request contains accepted Spans exceeding either the 4 MiB or 512-Span write-block limit
- **THEN** each database transaction SHALL contain no more than the configured byte and Span limits
- **AND** no other admitted request SHALL execute a database write while a block transaction is active

#### Scenario: A request completes successfully
- **WHEN** every write block for an admitted OTLP request commits
- **THEN** the receiver SHALL send its OTLP success response only after the request's data has been committed or non-retryably classified
- **AND** no later worker or queue SHALL continue persisting data from that request

### Requirement: Trace summaries remain correct with linear write work

For every committed write block, the receiver SHALL persist each accepted Span idempotently and SHALL replace an existing `(trace_id, span_id)` row with the later received row. The receiver SHALL maintain the associated Trace summary with the correct span count, earliest start time, latest end time, and first stored service name for the resulting Span rows. The aggregate-maintenance work for an ingestion request SHALL grow linearly with its accepted Span count and MUST NOT rescan all Spans of a Trace once for each received Span.

#### Scenario: One Trace is received across multiple blocks
- **WHEN** a request writes Spans for the same Trace in more than one write block
- **THEN** the resulting Trace summary SHALL include all committed Spans with correct count and time bounds
- **AND** the Trace service name SHALL remain the first stored service name

#### Scenario: A large single-Trace batch is persisted
- **WHEN** an accepted request contains increasing numbers of Spans for the same Trace
- **THEN** operation-count verification SHALL show aggregate-maintenance work proportional to the received Span count
- **AND** verification SHALL fail if the receiver rescans that Trace's persisted Spans once per received Span

#### Scenario: A duplicate Span corrects persisted data
- **WHEN** a later request supplies an existing `(trace_id, span_id)` with corrected attributes or time bounds
- **THEN** the persisted Span row and its Trace summary SHALL reflect the later received values
- **AND** the Trace span count and first stored service name SHALL remain correct

### Requirement: Storage protection produces final non-retryable OTLP outcomes

When storage protection intentionally refuses parsed Spans, the receiver SHALL return HTTP `200` with OTLP `partial_success`; `rejected_spans` SHALL equal the refused Span count plus any other non-retryable dropped Span count. The receiver SHALL publish received, saved, rejected, and dropped results from the final request outcome to runtime observability. An intentional storage-protection refusal MUST NOT request exporter retry or retain the request for later persistence.

#### Scenario: Storage admission refuses an accepted request's Spans
- **WHEN** storage protection refuses parsed Spans because the telemetry storage budget cannot accept them
- **THEN** the receiver SHALL return HTTP `200` with `partial_success` and the exact refused Span count
- **AND** runtime observability SHALL record those Spans as rejected rather than saved or dropped

### Requirement: OTLP responses preserve the recognized request encoding

Every response to a recognized OTLP trace request SHALL use the request's normalized encoding independently of `Accept`: JSON requests use `application/json`, and `application/x-protobuf` or `application/protobuf` requests use canonical `application/x-protobuf`. Full success SHALL return the empty standard `ExportTraceServiceResponse` in that encoding. Partial success SHALL return a standard `ExportTraceServiceResponse.partial_success`. A recognized request rejected for decoded size or temporary admission SHALL return a `google.rpc.Status` with code `8` (`RESOURCE_EXHAUSTED`), no `details`, and a message of at most 256 characters in that encoding: HTTP `413` SHALL use `Decoded telemetry request exceeds 16 MiB.` and HTTP `429` SHALL use `Telemetry receiver is at capacity.` with `Retry-After: 1`.

#### Scenario: A protobuf request is rejected for temporary admission pressure
- **WHEN** an `application/x-protobuf` OTLP trace request cannot obtain an admission slot
- **THEN** the receiver SHALL return HTTP `429` with `Retry-After: 1` and `application/x-protobuf`
- **AND** the response body SHALL decode as `google.rpc.Status` code `8` with no details and message `Telemetry receiver is at capacity.`

#### Scenario: A JSON request exceeds the decoded size limit
- **WHEN** an `application/json` OTLP trace request exceeds 16 MiB after decoding
- **THEN** the receiver SHALL return HTTP `413` with `application/json`
- **AND** the response body SHALL contain `google.rpc.Status` code `8`, no details, and message `Decoded telemetry request exceeds 16 MiB.`

#### Scenario: A JSON request completes with a partial success
- **WHEN** an `application/json` OTLP trace request has non-retryably rejected or dropped Spans
- **THEN** the receiver SHALL return HTTP `200` with `application/json`
- **AND** the response SHALL contain protobuf-JSON `partialSuccess.rejectedSpans` and `errorMessage`

### Requirement: Receiver work cannot feed telemetry back into the receiver

OTLP request handling and its trace-storage writes SHALL NOT generate outbound trace telemetry that is exported to the built-in OTLP receiver. This exclusion SHALL hold for successful ingestion, overload rejection, and storage-protection rejection.

#### Scenario: The receiver processes a trace export
- **WHEN** the built-in receiver accepts, rejects, or persists an OTLP trace request
- **THEN** its route handling and storage work SHALL NOT create a new trace export addressed to the built-in receiver
- **AND** repeated ingestion SHALL not form a recursive telemetry chain
