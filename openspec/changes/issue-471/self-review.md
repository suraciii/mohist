# Self Review

## Findings

### P1: First-write-wins silently removes the existing Span replacement contract

`design.md` D4 changes duplicate `(trace_id, span_id)` handling to `ON CONFLICT DO NOTHING` and explicitly states that changed duplicates are not revised. `tasks.json` T-002 locks that behavior into its acceptance criteria. The current `TraceIngester` uses `INSERT OR REPLACE` for Span rows and recomputes the Trace header so a re-arriving batch can self-heal (`packages/server/src/Mohist.Server/Otel/TraceIngester.cs:220-290`). Neither the issue, proposal, nor capability spec declares removal of that behavior as a breaking change. A later export of the same identity with corrected attributes or times would now leave the persisted Span stale, contrary to current observable behavior.

The plan must either preserve replacement/self-healing semantics with a linear aggregate algorithm, or explicitly declare and specify the breaking first-write-wins behavior, including its exporter-facing consequences and migration rationale.

### P2: The 413/429 OTLP error wire contract is not testable

The capability spec requires an "OTLP-compatible error response" for recognized `413` and `429` requests, but does not define the error message type, `google.rpc.Status` code, bounded message rules, or whether `details` is absent. `design.md` D5 and `tasks.json` T-001 repeat only the encoding requirement. `OtlpTraceResponseWriter` currently requires an explicit status code to encode an error (`packages/server/src/Mohist.Server/Otel/OtlpTraceResponseWriter.cs:92-128`), so implementation agents cannot determine the required protobuf/JSON payload or write a decisive wire assertion for the new overload paths.

The spec and design must define the standard error envelope and exact status code/message constraints for both 413 and 429, in addition to HTTP status, `Retry-After`, and request-derived encoding.

<promise>FAIL</promise>
