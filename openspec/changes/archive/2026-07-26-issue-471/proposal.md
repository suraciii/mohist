## Why

The OTLP receiver currently buffers every request without a size or admission limit, writes an entire batch in one SQLite transaction, and recomputes Trace aggregates for every Span. A large or concurrent export can therefore let observability data consume unbounded memory and write time, threatening the Server that it is meant to diagnose.

## What Changes

- Bound decompressed OTLP trace requests to 16 MiB before complete buffering, returning `413` for requests that exceed the limit in either JSON or protobuf encoding.
- Admit at most four OTLP requests before reading their bodies and allow only one admitted request to write at a time; reject temporary excess with `429` and `Retry-After` rather than retaining a post-response queue.
- Write accepted trace data in transactions capped at 4 MiB or 512 Spans, whichever comes first, while preserving correct Trace summaries across request blocks.
- Make Trace summary updates linear in the received Span count rather than rescanning the same Trace for each Span.
- Report storage-budget refusals as non-retryable OTLP `partial_success` with accurate rejected counts, and continue publishing saved, rejected, and dropped outcomes to the existing runtime observability status.
- Preserve OTLP response encoding for recognized JSON and protobuf requests, including success, partial-success, and error responses.

## Capabilities
- `otlp-trace-ingestion`: Bounded OTLP trace request admission, block-based linear persistence, storage-rejection outcomes, and protocol-correct JSON/protobuf responses without deferred ingestion queues.

## Impact

- **Server OTLP route and ingestion** (`packages/server/src/Mohist.Server/Api/OtlpRoutes.cs`, `packages/server/src/Mohist.Server/Otel/TraceIngester.cs`): enforce request admission and size limits, serialize writes, and persist bounded blocks with correct aggregate maintenance.
- **OTLP response and runtime status integration** (`packages/server/src/Mohist.Server/Otel/OtlpTraceResponseWriter.cs`, ingest outcome/protection types, `RuntimeObservability`): expose overload and storage-protection outcomes through OTLP and the existing status contract.
- **Server telemetry tests** (`packages/server/tests/Mohist.Server.SpecTests/Specs/Telemetry/`, `packages/server/tests/Mohist.Server.UnitTests/Telemetry/`): add deterministic admission, block-boundary, aggregate-correctness, operation-count, and wire-encoding coverage.
- No new external services, message queues, or persistence stores are introduced.
