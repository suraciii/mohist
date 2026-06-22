# Capability: server-otel-tracing

## Purpose

TBD

## Requirements

### Requirement: Inbound HTTP requests are traced

The server SHALL emit one OpenTelemetry span for each inbound ASP.NET Core HTTP request it handles. Each span SHALL record the HTTP route template (or fallback route when unmatched), the HTTP response status code, and the request duration. Inbound HTTP instrumentation SHALL be produced by the standard ASP.NET Core automatic instrumentation source, not custom code.

#### Scenario: Inbound HTTP request produces a span with route and status
- **WHEN** a client sends an HTTP request to a mapped server route (e.g. `POST /api/issues`)
- **THEN** the server SHALL emit exactly one inbound HTTP span for that request
- **AND** the span SHALL carry the matched route template as an attribute
- **AND** the span SHALL carry the final HTTP response status code
- **AND** the span SHALL reflect the wall-clock duration of request handling

#### Scenario: The server's own OTLP/otel ingest path is never traced
- **WHEN** the server POSTs traces to its own collector at the `/otel/` path (the #219 same-process collector on port 4318)
- **OR** any other request arrives under the `/otel/` path prefix
- **THEN** inbound HTTP instrumentation SHALL NOT emit a span for that request
- **AND** no trace SHALL be produced for the `/otel/` ingest path so that "server sends itself a trace" cannot feed back into another trace-send

### Requirement: SignalR hub method invocations are traced as child spans

The server SHALL emit one span for each SignalR hub method invocation, produced by the built-in .NET `Microsoft.AspNetCore.SignalR.Server` ActivitySource. Each hub-method span SHALL be created as a child of the calling activity context so it belongs to the same trace as the transport that carried the connection.

#### Scenario: SignalR hub dispatch produces a child span
- **WHEN** a SignalR hub method is invoked (e.g. a runner dispatch or a web client event)
- **THEN** the server SHALL emit one hub-method span for that invocation
- **AND** the span SHALL have its parent set to the active activity context of the carrying connection
- **AND** the span SHALL belong to the same trace as the inbound request that established the interaction

### Requirement: Orleans grain calls are traced as child spans

The server SHALL emit spans for Orleans grain invocations (direct grain calls, persistence, and reminders) using the Orleans 10 native ActivitySource. Grain-call spans SHALL attach as children of the calling activity context so the grain portion of an execution chain is observable.

#### Scenario: Grain invocation triggered by a hub or request is traced
- **WHEN** a hub method or request handler triggers an Orleans grain call
- **THEN** the server SHALL emit a grain-invocation span for that call
- **AND** the span SHALL be a child of the calling activity context
- **AND** the grain call chain SHALL be observable within the same trace as its caller

### Requirement: EF Core database queries are traced with SQL text

The server SHALL emit one span for each EF Core database query it executes, using the standard EF Core automatic instrumentation source. Each query span SHALL carry the SQL statement text and the query duration, and SHALL be created as a child of the calling activity context (e.g. the grain that issued the query).

#### Scenario: EF query issued inside a grain produces a deeper child span
- **WHEN** a grain executes an EF Core query against the database
- **THEN** the server SHALL emit one query span for that SQL statement
- **AND** the span SHALL carry the SQL text as an attribute
- **AND** the span SHALL carry the query duration
- **AND** the span SHALL be a child of the grain-invocation activity, forming a deeper level in the same trace

### Requirement: Outbound HttpClient calls are traced

The server SHALL emit one span for each outbound `HttpClient` call it makes (e.g. self-update checks, readiness probes), using the standard .NET `HttpClient` automatic instrumentation source. Each outbound-call span SHALL attach as a child of the calling activity context.

#### Scenario: Outbound HTTP call produces a child span
- **WHEN** the server makes an outbound `HttpClient` call (e.g. a readiness probe or self-update check)
- **THEN** the server SHALL emit one outbound HTTP span for that call
- **AND** the span SHALL carry the destination URI and response status code
- **AND** the span SHALL be a child of the activity context that initiated the call

### Requirement: Traces form one unbroken execution chain across all segments

For a single request whose execution flows through the instrumented segments, the server SHALL produce exactly one trace spanning all of them. HTTP, SignalR hub, Orleans grain, EF Core query, and outbound HttpClient spans SHALL be linked as parent-child along the real execution path so that no segment appears as a disconnected, parentless span.

#### Scenario: A full request yields a single trace with correct parentage
- **WHEN** an inbound HTTP request dispatches via SignalR, triggers an Orleans grain, which issues an EF Core query, and the grain issues an outbound HttpClient call
- **THEN** the server SHALL emit all of those spans under a single shared trace id
- **AND** each non-root segment SHALL carry the correct parent span id of the segment that synchronously caused it
- **AND** no segment SHALL appear as an orphan span with no parent when a causal parent exists

### Requirement: Traces are exported over OTLP HTTP to a configurable endpoint

The server SHALL export traces using the OpenTelemetry OTLP HTTP exporter. The exporter endpoint SHALL default to `http://localhost:4318/otel` (the #219 same-process collector ingest). The endpoint SHALL be configurable through the server's existing configuration system and overridable via `MOHIST__*` environment variables, and SHALL also accept an external collector URL.

#### Scenario: Default endpoint targets the local collector
- **WHEN** the server starts with no OTel endpoint configured
- **THEN** traces SHALL be exported via OTLP HTTP to `http://localhost:4318/otel`

#### Scenario: Endpoint is overridden by configuration and environment variable
- **WHEN** the server's `Mohist:Otel` config section sets an endpoint, or a `MOHIST__Otel__Endpoint` environment variable is set
- **THEN** the exporter SHALL send traces to that endpoint instead of the default
- **AND** the environment variable SHALL take precedence over the config-file value

### Requirement: Exporter failure is non-fatal

The server SHALL continue normal operation when the configured OTLP endpoint is unreachable or returns an error. Exporter failure SHALL NOT throw exceptions into request paths, SHALL NOT block or delay request handling, and SHALL NOT crash or restart the server.

#### Scenario: Endpoint unreachable does not affect server behavior
- **WHEN** the configured OTLP endpoint is unreachable (connection refused, DNS failure, timeout)
- **THEN** the server SHALL NOT throw exceptions on any request path
- **AND** request handling SHALL NOT be blocked or delayed by exporter retry/timeout behavior
- **AND** the server SHALL continue to start, run, and serve requests normally

### Requirement: Instrumentation can be fully disabled with a master switch

The server SHALL provide a master on/off switch for all OpenTelemetry instrumentation, configurable through the existing config system with `MOHIST__*` environment-variable override. When the switch is off, the server SHALL produce no spans, SHALL NOT initialize export, and SHALL behave identically to a server that has no instrumentation wired in. Existing server behavior SHALL be zero-regression when instrumentation is disabled.

#### Scenario: Master switch off produces and sends nothing
- **WHEN** instrumentation is disabled via the master switch
- **THEN** the server SHALL NOT emit any span for any of the five instrumentation sources
- **AND** the server SHALL NOT attempt any OTLP export

#### Scenario: Disabled instrumentation has no behavioral impact
- **WHEN** instrumentation is disabled and the server processes its normal workload
- **THEN** the server's request handling, SignalR dispatch, Orleans execution, and database behavior SHALL be unchanged
- **AND** no error, latency regression, or functional change SHALL be observable compared to a server built without this capability

### Requirement: Only automatic, trace-only instrumentation

The server SHALL emit traces only, and SHALL source every span from community / built-in automatic instrumentation backed by standard `ActivitySource`. The server SHALL NOT emit logs or metrics via OpenTelemetry, SHALL NOT add custom business spans, and SHALL NOT apply trace sampling in this version (the first version exports the full trace volume).

#### Scenario: No custom business spans are introduced
- **WHEN** the server is instrumented and running
- **THEN** every span emitted SHALL originate from a standard automatic instrumentation source (ASP.NET Core, SignalR, Orleans, EF Core, or HttpClient)
- **AND** no custom business span SHALL be created by application code

#### Scenario: Only traces are emitted, not logs or metrics
- **WHEN** the OpenTelemetry SDK is enabled on the server
- **THEN** the server SHALL emit trace telemetry only
- **AND** the server SHALL NOT configure the OpenTelemetry logging or metrics pipelines
