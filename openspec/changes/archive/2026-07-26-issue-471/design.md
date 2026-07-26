## Context

The OTLP route currently reads every JSON request into a string and every protobuf request into a `MemoryStream` before parsing. `TraceIngester` then materializes all normalized Spans, opens one SQLite transaction for the request, and recomputes each Trace aggregate by querying all of that Trace's persisted Spans after every Span upsert. Consequently, a sender controls request memory, write-lock duration, and aggregate-query amplification.

The existing `IngestOutcomeBuilder`, `RuntimeObservability`, `BudgetAwareIngestProtectionDecision`, and `OtlpTraceResponseWriter` already provide one final outcome/publication boundary and encoding-aware OTLP responses. The design adds bounded admission and persistence beneath those interfaces while retaining `otel.db` schema and the existing collector-port isolation. Operators need the collector to degrade independently of Workflow and Runner work; exporters need overload and permanent-loss results that do not cause ambiguous retries.

Constraints:

- The fixed budgets are 16 MiB decoded request content, four admitted requests, one database writer, and 4 MiB or 512 Spans per write block.
- No post-response memory queue, durable queue, second database, or external broker is permitted.
- Tests use TestServer, in-memory shared SQLite, fake time, controlled streams, and awaitable signals. They do not use sockets, wall-clock waits, or host filesystems.
- The established `OtelSuppressionMiddleware` and exporter filter remain the self-observation authority.

## Goals / Non-Goals

**Goals:**

- Apply the proposal's resource limits before an OTLP body is fully buffered.
- Bound SQLite lock time while preserving idempotent Span storage and accurate Trace summaries across blocks.
- Preserve the spec's JSON/protobuf response contract and publish one final telemetry outcome for each parsed request attempt.
- Make resource and cost assertions deterministic through explicit test seams.

**Non-Goals:**

- Add tail sampling, user-configurable ingest budgets, a queue, a broker, or another telemetry database.
- Change retention, storage-budget arbitration, runtime-status schema, or the core health endpoint.
- Guarantee lossless telemetry after storage protection or an unexpected write failure.
- Change the stable `traces` or `spans` table and column schema used by `mo otel` readers.

## Decisions

### D1. A singleton OTLP ingest gate owns both admission and write serialization

Add an internal `OtlpIngestGate` singleton with four non-waiting request leases and one writer lease. After validating that a `Content-Type` is recognized, `OtlpRoutes` attempts to acquire a request lease before reading `HttpRequest.Body`. Failure returns a writer-produced `429` with a fixed `Retry-After`; the body is untouched. A request lease is released in `finally` after parsing, writing, response generation, or cancellation. The admitted request awaits the single writer lease only after its parse/protection classification is complete, so at most four requests consume receiver resources and only one can hold SQLite's write path.

The gate exposes internal, signal-controlled test support that can hold request or writer leases and observe acquisition. It has no queue or background worker: an admitted request is the only owner of its data, and its handler remains responsible for completing it.

Alternative considered: a bounded `Channel` with a background writer. Rejected because it would either retain telemetry after the response or require a second acknowledgement protocol, both of which obscure ownership and defer pressure instead of bounding it.

### D2. Request decompression precedes a counting, fail-closed body reader

Register ASP.NET Core request decompression and place its middleware before route execution. The OTLP route consumes the resulting decoded stream through a `LimitedOtlpBodyReader`, which stops after 16 MiB without appending the excess byte. JSON deserializes directly from this limited stream; protobuf copies only the limited content into its parser buffer. Invalid compressed content is treated as an invalid recognized request. A size-limit exception maps to an encoding-aware `413`; decode failures map to the existing encoding-aware invalid-argument response. Neither path constructs an `IngestOutcome`.

Alternative considered: rely on `Content-Length` or Kestrel's request limit. Rejected because `Content-Length` is optional and compressed payload size does not bound decoded memory. Alternative considered: retain `ReadToEndAsync`/`CopyToAsync` and check length after reading. Rejected because the limit would be ineffective against the allocation it is meant to prevent.

### D3. Block planning uses normalized persisted-data weight and a single oversized-Span rule

`TraceIngester.Prepare` continues to produce immutable normalized Span classifications. A block planner walks accepted Spans in arrival order, adding each Span's deterministic UTF-8 weight for the columns that will be persisted. It closes a block before adding a Span that would exceed 4 MiB or 512 Spans. A single normalized Span whose weight alone exceeds 4 MiB becomes a non-retryable dropped classification with a bounded reason; it is not written in an oversized transaction. The planner does not retain multiple request copies and each planned block is released after its transaction completes.

The byte budget deliberately measures the normalized stored representation rather than SQLite page growth or compressed wire bytes. This provides a stable, testable upper bound before the transaction begins and includes attributes/resource attributes that dominate row size.

Alternative considered: use SQLite file growth as the block meter. Rejected because WAL/page allocation is not attributable to one row or transaction and cannot prevent an oversized transaction. Alternative considered: allow one oversized Span as an exception. Rejected because it breaks the only hard per-transaction memory/work bound.

### D4. Replace Span rows and refresh each affected Trace through indexed block-local work

Within each writer lease, a block preserves the current `INSERT OR REPLACE` behavior. The block first groups duplicate identities so its last received row wins, reads existing identities in trace-scoped batches, and records the count of identities that are new before replacing every grouped Span row. It then updates each affected Trace header once: a new header receives the first encountered service name, an existing header preserves it, and `span_count` increases only by the number of new identities. The replacement therefore continues to self-heal corrected attributes and time bounds without inflating count.

`OtelDb` adds two additive indexes, `(trace_id, start_time)` and `(trace_id, end_time)`. After the row replacements, each affected Trace obtains its earliest start and latest end through indexed `ORDER BY ... LIMIT 1` reads, rather than a `MIN`/`MAX`/`COUNT` scan for every input Span. The resulting work is bounded by the block's input identities and affected Traces, not by repeated full-Trace scans. The tables, columns, primary key, existing index names, and CLI reader contract remain unchanged; `CREATE INDEX IF NOT EXISTS` materializes the new indexes for existing stores without a data migration.

Alternative considered: preserve `INSERT OR REPLACE` and recompute `COUNT`/`MIN`/`MAX` after every Span. Rejected because it retains the observed quadratic Trace scan. Alternative considered: first-write-wins insertion. Rejected because it silently removes the existing correction/self-healing behavior for a repeated Span identity.

### D5. A request attempt publishes one outcome after all blocks reach a terminal result

If every block commits, `TraceIngester` builds one committed outcome from the final classifications: saved equals every accepted parsed attempt, including a duplicate that replaces an existing row, and protection rejections/drops yield `partial_success`. If a storage-protection decision closes admission before a Span is planned, that Span remains a rejected classification and no write is attempted for it. If a write block throws or the request is cancelled, the active block rolls back and the request publishes the existing retryable or cancelled outcome semantics. Earlier blocks can already be committed; the response still asks retry on an unexpected write failure, and replacement-based replay safely completes the missing blocks. A retryable failed attempt does not publish saved/rejected/dropped counts, preserving the existing request-attempt accounting rule.

`OtlpTraceResponseWriter` remains the sole response encoder. Add explicit overload writers for decoded-size and temporary-admission rejection. They encode `google.rpc.Status` code `8` (`RESOURCE_EXHAUSTED`) with no details and bounded fixed messages: `Decoded telemetry request exceeds 16 MiB.` for HTTP `413`, and `Telemetry receiver is at capacity.` for HTTP `429`. The `429` writer sets the delay-seconds header `Retry-After: 1`. Both select JSON or canonical protobuf from the normalized request content type; `Accept` cannot change them. Existing full-success, partial-success, invalid-input, and retryable-storage paths retain their current encoding rules. The route and storage path remain inside the current suppression scope, so no new instrumentation path is added.

Alternative considered: add a multi-block aggregate outcome model that reports partial commits as saved on a `503`. Rejected because it would make exporter retry accounting and runtime counters ambiguous; idempotent replay gives a simpler, already-supported convergence path.

### D6. Tests prove boundaries through controlled collaborators and operation counts

Extend the OTLP TestServer fixture with replaceable gate and body-reader dependencies. A controlled stream signals its first read and blocks or exceeds the decoded limit; gate fakes signal slot ownership and writer entry. Integration specs cover JSON and protobuf `413`/`429`, assert the fifth request has not read its body, and decode responses in their request encoding. `TraceIngester` specs use in-memory SQLite plus a command/aggregate probe to assert transaction block count, writer exclusivity, cross-block Trace summaries, duplicate behavior, and linear aggregate operations. Cancellation and injected write failures use `TaskCompletionSource` signals instead of delays.

Alternative considered: test capacity with real concurrent HTTP timing and inspect SQLite locks. Rejected because scheduler timing and physical database behavior would make the suite flaky and violate the repository's test constraints.

## Risks / Trade-offs

- [An admitted request can wait for the writer while holding a request slot] -> The wait is bounded by four slots, and the writer itself is bounded by the block caps; a fifth request receives immediate `429` rather than unbounded retention.
- [A storage failure can occur after earlier blocks commit] -> Return the established retryable response and rely on replacement-based replay; runtime counts only final successful attempts as saved.
- [Correcting a replaced Span can move a Trace boundary] -> Refresh only that Trace's two boundaries through the additive composite indexes after the block, rather than rescanning it for every replaced Span.
- [Decoded-size enforcement cannot avoid all parser allocations below 16 MiB] -> The capped reader prevents unbounded body buffering; normal parsing remains bounded by the same fixed maximum.
- [Fixed budgets may reject a legitimate exporter burst] -> `429` is explicit and retryable, while storage-budget rejection uses `partial_success` so senders do not amplify permanent pressure.

## Migration Plan

1. Add the gate, limited reader, block planner, additive Trace-boundary indexes, and replacement-preserving block writer behind the existing OTLP route and DI registration.
2. Add deterministic unit and integration coverage for all resource, persistence, outcome, encoding, and suppression requirements.
3. Deploy with the collector still following its existing enabled setting; no table/column or data migration is required, and existing `OtelDb` initialization creates the additive indexes.
4. Monitor existing runtime observability rejected/dropped and storage-write degradation fields after enabling collection.
5. Roll back by reverting the Server deployment. Existing `traces` and `spans` rows remain readable because tables and columns are unchanged; the additive indexes are harmless if retained.

## Open Questions

None. The fixed limits, overload status codes, storage-protection behavior, and response-encoding requirements are established by the proposal and capability spec.
