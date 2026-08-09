# Observability

Observability helps find and explain runtime problems. It is not a business
fact and does not participate in business decisions.

## Boundary

- Business code can emit an observability signal but cannot wait for the
  observability system to finish.
- Collection, export, query, and storage failures do not change a business
  result.
- Observability data can be lost. Business data cannot be lost to protect it.
- Requests and writes made by observability itself must not emit the same class
  of signal recursively.

## Signal Responsibilities

Metrics find problems. At minimum, they cover:

- Request count and duration, aggregated by stable route name.
- Database and downstream call counts per request.
- Process CPU, memory, and garbage-collection pressure.
- Received, stored, overload-rejected, and unexpectedly dropped observability
  data.
- Current observability-store size and growth rate.

Traces explain one operation. A slow request, failed request, or request with
an unusual number of downstream calls must link to its complete Trace. Logs
record discrete events such as port-binding failure, batch rejection, dropped
data, and degraded storage.

The built-in collector does not perform Tail Sampling. It stores complete
received Traces and deletes the oldest complete Trace when the retention budget
is reached. A sender that needs sampling by failure, duration, or attribute
uses an external OpenTelemetry Collector. Mohist does not duplicate a stateful
sampler.

Metric labels use only stable, bounded-cardinality values. Project ID, Issue
number, WorkflowRun ID, AgentSession ID, and raw URL do not enter labels. Put
them in a Trace or log when needed.

The Server also maintains a bounded diagnostic summary for the latest five
minutes, with at most ten anomalous routes. It uses stable route names and
shows request count, duration, and database and downstream calls per request.
`mo otel status` reads it. The summary is not written to the business database
and starts over after a Server restart.

## Logs

An Agent is the primary log reader. A line format must work directly with tools
such as `rg` without learned parsing and must also parse unambiguously with a
standard logfmt parser. Human readability is the third goal and applies when it
does not conflict with the first two.

### Line Contract

Logs use strict logfmt in the format of Go slog TextHandler. Every record
occupies exactly one line.

- Leading keys have fixed order: `time`, `level`, and `msg`. Attributes follow,
  with `service` and `component` before domain keys.
- `time` is RFC 3339 UTC with millisecond precision, such as
  `2025-01-15T10:30:45.123Z`.
- `level` is `TRACE`, `DEBUG`, `INFO`, `WARN`, `ERROR`, or `FATAL`.
- `msg` is a short human-readable statement of what happened.
- A value containing whitespace, `=`, a quote, or a non-printable character is
  double-quoted and escapes content with `\n`, `\"`, and `\\`. Numbers,
  Booleans, and simple identifiers remain bare.
- A newline appears only as escaped `\n` inside a quoted value. Complete
  exception information, including type, message, and stack, goes in the
  `exception` key and remains on one line.
- Omit a key that has no value.

```
time=2025-01-15T10:30:45.123Z level=INFO msg="work claimed" service=server component=dispatch work=w_abc run=r_123 issue=468
time=2025-01-15T10:30:46.567Z level=ERROR msg="report failed" service=runner component=report work=w_abc attempt=3 exception="HttpRequestException: connection refused\n   at RunnerClient.Report(...)"
```

### Field Vocabulary

- Domain keys are lowercase words: `issue`, `run`, `work`, `session`, `job`,
  `runner`, `attempt`, `path`, and `reason`. Every process uses the same key for
  the same meaning.
- A log-template parameter, such as `WorkId` in
  `LogWarning(ex, "report failed for {WorkId}", id)`, must become an independent
  key and cannot exist only as interpolation in `msg`.
- `component` is a short word such as `dispatch` or `cleanup`, projected from
  the log category rather than a complete class name. Take the final category
  segment, remove a `Service`, `Grain`, `Handler`, `Routes`, or `Provider`
  suffix, and lowercase the first letter. For example,
  `DispatchService -> dispatch` and `RunnerHub -> runnerHub`.
- `service` identifies the writing process, `server` or `runner`, and matches
  the log filename.
- Domain IDs appear only in log keys and Trace attributes, never metric labels.

### Files and Retention

- The default log directory is `$HOME/.mohist/logs`. The Server can override it
  with `Mohist:LogsPath`, and the Runner with `MOHIST_LOGS_PATH`.
- Server writes `server.log`; Runner writes `runner.log`. Both use the same line
  contract.
- Files rotate by size. One file is limited to 32 MiB, and the current file plus
  two historical generations are retained, such as `server.log`,
  `server.log.1`, and `server.log.2`. Rotated files remain uncompressed so line
  tools can read them directly.
- Runner process diagnostics use the same line contract in `runner.log` and are
  also passed through to the terminal for development observation. There is no
  terminal-only diagnostics path.

### Read Path

- `/api/logs/tail` parses logfmt one line at a time into `LogEntry`. The file
  format is the single source of truth; there is no second on-disk format.
- A line that fails parsing is not dropped. It becomes the unmodified `message`,
  structured fields remain empty, and `raw` retains the source line.

## Resource Budgets

Every queue, batch, and store has a hard limit. Defaults support long-running
single-host use:

- Retain a Trace for at most 72 hours.
- Use at most 1 GiB for Trace storage.
- Limit one decompressed OTLP request to 16 MiB.
- Allow at most four OTLP requests to hold receive resources concurrently, with
  at most one database writer.
- Process at most 4 MiB or 512 Spans in one database write, whichever comes
  first.

When the time or space budget is exceeded, delete the oldest complete Trace
first. If space cannot be reclaimed in time, stop writing new observability
data, count overload rejections, and return the rejected Span count through
OTLP `partial_success`. The sender does not retry that batch. Return `413`
before reading the complete request body when it exceeds the request limit.
Return `429` with `Retry-After`, also before reading the body, when receive
concurrency is full. Observability overload cannot block requests, Workflow
scheduling, or Runner communication.

The storage budget includes the database and auxiliary files. One internal
write block may briefly exceed the limit, but storage cannot grow continuously.
The request-size limit applies before fully buffering the body and uses the
decompressed size when compression is enabled. Disabling observability stops
collection, export, and background cleanup work.

## Runtime Status

Observability has three states:

- `off`: The user disabled observability.
- `healthy`: Collection and storage work without triggering protection.
- `degraded`: Collection or storage is unavailable, or data is being dropped.

Status also reports storage usage and budget, received and stored counts,
overload rejection and unexpected drop counts, the latest degradation reason,
current process resource pressure, and the bounded route summary.
Observability degradation does not mean that the business service is
unavailable, but runtime status must show it clearly.

## Enablement Gate

Built-in observability can be enabled by default only when request, write, and
storage limits are all enforced and runtime status exposes overload, drops, and
storage pressure. It remains off while any protection is incomplete. Operation
cannot depend on a user cleaning it periodically.

## High-frequency Paths

Polling, status queries, heartbeats, and background scans execute repeatedly.
Their cost can grow only with currently relevant data, not unrelated history.

These paths expose candidate count, processed count, and downstream call count.
When a small response causes many database or cross-process calls, metrics and
Traces must show that amplification directly.

## Verification

- Automated tests lock self-observation filtering to prevent a feedback loop.
- Receive tests prove that decompressed request and concurrency-admission limits
  apply before full buffering and that internal writes are budgeted chunks.
- Storage tests prove time and space limits and overload-drop behavior, with the
  database, WAL, and SHM included in the budget.
- High-frequency paths verify cost through operation counts rather than wall
  time.
- Tests use the same current data with different amounts of history. More
  history cannot amplify queries or calls.

## Current Gaps

Metrics, the bounded route diagnostic summary, and `mo otel status` runtime
status are implemented. Remaining gaps:

- There is no automatic anomaly notification. Metrics find problems, but an
  anomalous route appears only in status and is not surfaced proactively.
- The log line contract is not implemented. Server logs remain NDJSON,
  template parameters are not extracted into keys, and retention does not
  rotate. Runner diagnostics write only to the terminal and not to a file.
