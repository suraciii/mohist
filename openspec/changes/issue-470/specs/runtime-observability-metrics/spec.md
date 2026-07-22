### Requirement: Runtime signals are available through a standard Meter and a local summary

Mohist SHALL emit runtime observability signals through a standard .NET `Meter` and SHALL update an in-process summary from the same observations. The signals SHALL cover stable-route request count and latency, database-call and downstream-call count, current process CPU, working set and GC heap pressure, telemetry received, saved, rejected and dropped, and observability storage usage and growth. A request observation SHALL close atomically at response completion; work that executes later in a detached background context MUST NOT mutate that completed request. Reading the local summary SHALL NOT depend on an external metrics backend or on exporting the local metrics to the built-in collector.

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

### Requirement: Metric identity has stable low cardinality

Metric instrument names and the exact label-key set accepted by each instrument SHALL be stable, explicitly test-locked contracts. Route dimensions SHALL use the matched route template or another stable bounded route name, never a concrete request URL. Project identifiers, issue numbers, WorkflowRun identifiers, AgentSession identifiers, raw URLs, trace identifiers, span identifiers and other per-instance identities MUST NOT appear as metric labels. A request for which no stable route name is available SHALL use one bounded fallback identity rather than its raw path.

#### Scenario: Requests differ only by resource identity

- **WHEN** requests target the same route template with different project, issue, workflow or session values
- **THEN** their runtime metrics SHALL use the same route label value
- **AND** none of those resource identities SHALL appear in any metric label

#### Scenario: An unmatched URL is observed

- **WHEN** a request does not resolve to a stable route template
- **THEN** its metric SHALL use a bounded fallback route identity
- **AND** the raw URL or path SHALL NOT become a label value

#### Scenario: The metric contract changes unintentionally

- **WHEN** an implementation adds or renames an instrument or adds a label key outside the declared metric catalog
- **THEN** the metric contract test SHALL fail

### Requirement: Local ranked route diagnostics are bounded and ephemeral

The local runtime summary SHALL retain observations from a five-minute window at one-second resolution and SHALL be bounded independently of request volume, route count and telemetry history. The single boundary bucket MAY retain observations for less than one additional second so an observation is never discarded before it is five minutes old. The summary SHALL produce at most 10 route entries ranked without an anomaly threshold: first by `database calls per request + downstream calls per request` descending (one call of either kind has equal weight), then by average latency descending, then by stable route name using ordinal ascending order. Each entry SHALL contain the stable route name, request count, latency information, database calls per request and downstream calls per request. The retained summary SHALL reset when the Server process restarts and MUST NOT be written to the business database or treated as a Workflow or Session fact.

#### Scenario: More than ten routes are active

- **WHEN** more than 10 stable routes have observations within the current five-minute window
- **THEN** the local diagnostic result SHALL contain no more than 10 route entries
- **AND** its retained memory SHALL remain within a fixed bound

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
