# Observability

Observability helps users find and explain runtime problems. It must not slow,
block, or change product work. See [Observability design](../design/observability.md)
for storage, signal, and protocol contracts.

## Product Commitments

- Users can see whether Mohist is `healthy`, `degraded`, or `off`.
- Users can identify slow, failing, resource-heavy, or dropped operations.
- Users can determine when a problem started and what it affects.
- Users can see the next inspection or recovery action.
- Issue, Workflow, AgentSession, and Runner work continue when observability
  fails or is disabled.
- Observation data is bounded and gives up space before it consumes resources
  needed by product work.

## Signal Types

Each signal has one job:

- **Metrics** show trends in request volume, duration, resource use, data
  growth, and dropped or rejected observation data.
- **Traces** explain one operation and show where time and effort were spent.
- **Logs** record a discrete failure, rejection, drop, or degradation and its
  cause.

Users should not need to inspect many traces to discover a problem.

## Safety Boundary

Observation data uses separate, bounded storage. It does not compete with
product data for disk space. When observation data grows too quickly, Mohist
reduces or drops it before it sacrifices core work.

The default configuration supports long-running use without regular manual
cleanup:

- Trace retention is 72 hours.
- Trace storage is limited to 1 GiB.
- The OTLP receiver listens on `localhost:4318` and is not exposed externally.
- Built-in observation is enabled by default.

Set `Mohist:Otel:Enabled=false` and restart Server to disable collection,
receiving, diagnostic sampling, and background maintenance when resource or
binding problems occur.

## Runtime Status

`mo otel status` reports:

- `healthy`: collection and storage work without protection;
- `degraded`: collection or storage is unavailable, or data is being dropped;
- `off`: the user disabled observability.

It also reports storage use, storage budget, received and stored counts,
rejected and unexpectedly dropped data, the latest degradation reason, current
resource pressure, and the latest bounded route summary.

Observability degradation does not mean that the business service is
unavailable. Status must make that distinction clear.

## Implementation Gaps

Metrics, the bounded route summary, `mo otel status`, and the Server and Runner
log contract are implemented. Mohist does not yet send automatic anomaly
notifications. An anomalous route appears in status but is not surfaced
proactively.
