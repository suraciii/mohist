# Self-Review - Issue 470

Reviewed the live issue, proposal, design, all three capability specs, the
eight-task graph, and the relevant current Server implementation.

## Verdict

The plan is not ready to build. Its product coverage and task graph are broadly
sound, but four contracts still leave materially different behavior or an
unexecutable verification requirement for the build agents to resolve.

## Findings

### F1 - A read-only probe can declare an unverified ingest path healthy (high)

The status spec defines `healthy` as meaning ingestion and storage are usable
(`specs/otel-runtime-status/spec.md:3`). D5's readiness probe only opens a
read/write-create connection, executes `PRAGMA schema_version`, and reads file
lengths (`design.md:112`). D6 then defines enabled with no active source as
healthy (`design.md:120-126`), and T-003 explicitly requires the first successful
storage probe to reach healthy (`tasks.json:64`).

A readable existing SQLite database can still be unwritable, so this sequence
can clear `storage_unverified` and report healthy before the ingest write path
has ever been established. The plan must choose and specify one coherent
contract: either verify write readiness without corrupting telemetry, retain an
unverified source until a real write succeeds, or redefine healthy as "no known
failure" in the spec and user-facing semantics.

### F2 - Protobuf partial-success and error responses have no wire contract (high)

T-002 requires JSON and protobuf ingestion to expose the same outcome categories
and requires partial-success responses for rejected or dropped spans
(`tasks.json:37-38`). The design defines classification and counters but never
defines response content negotiation or the protobuf
`ExportTraceServiceResponse` encoding (`design.md:53`).

This is not supplied by the current implementation: protobuf requests are
parsed manually, every success is returned as JSON, and only `JsonException` is
mapped to a whole-body 400 (`packages/server/src/Mohist.Server/Api/OtlpRoutes.cs:85-108`;
`packages/server/src/Mohist.Server/Otel/OtlpProtobuf/OtlpProtobufTraceParser.cs:7-29`).
The plan must specify content-type-specific success/partial-success bodies and
malformed-protobuf error mapping so an AFK task does not invent an OTLP protocol
contract.

### F3 - The fallback production-composition test is incompatible with test constraints (medium)

D7 correctly introduces fake hosts for lifecycle tests, but also assigns a
production-factory composition spec responsibility for proving one surviving
silo/sampler and exactly one storage probe (`design.md:136-142`; `tasks.json:160`).
The production factory configures Kestrel listeners and Orleans, while repository
tests may not use real sockets or system services (`design/testing.md:45-55`).
A non-starting composition test can verify registrations and listener intent,
but cannot prove the stated runtime facts; fake-host tests cannot prove the
production graph.

Split the assertions between the fake lifecycle boundary and static production
composition, or define an in-memory host/silo seam through which the production
factory can be started without real network resources.

### F4 - Request duration lacks an injectable time contract (medium)

The metric catalog and route ranking require request duration, but D3 specifies
only scope creation and atomic closure (`design.md:71-86`), and T-004 does not
state how elapsed time is measured (`tasks.json:79-89`). The repository requires
new time behavior to use injected `TimeProvider` and deterministic tests
(`design/testing.md:59-68`).

Specify that middleware captures and computes elapsed duration through the
injected `TimeProvider` timestamp APIs, including exceptional and cancelled
responses, and lock that behavior with fake-time tests.

## Coverage And Structure

- Proposal capabilities and spec directories match.
- The eight task dependencies are resolved and acyclic.
- The plan otherwise covers the issue's tri-state status, bounded route summary,
  process and storage pressure, telemetry outcomes, low-cardinality metric
  catalog, agent-path amplification, transition-only logs, self-observation
  exclusion, history-independent status cost, and core-health independence.

<promise>FAIL</promise>
