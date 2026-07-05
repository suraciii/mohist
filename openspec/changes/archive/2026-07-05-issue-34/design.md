## Context

The `/logs` page is the system-level log viewer for diagnosing the **server daemon**. Today it is broken end-to-end across three layers that disagree:

- **API contract mismatch.** `GET /api/logs/tail` returns `{ lines: object[], nextCursor }` where each line is an already-deserialized `JsonElement` (or `{ raw = line }`) — `Api/LogsRoutes.cs:45-51,57-61`. The Web client types the response as `{ file, cursor, lines: string[], truncated, reset }` — `pages/logs/model/api.ts:3-9`. Every field except `lines` is `undefined` at runtime, so the `File:` source line, truncation banner, and cursor-based pagination never fire. Worse, `useLogs.parseLogLine` runs `JSON.parse` on each element (`useLogs.ts:12-14`), but the server already returns objects — so `JSON.parse` is given an object, coerces it to `"[object Object]""`, throws, and falls back with `level/time/service = null` and `message = "[object Object]"`. Field extraction silently fails for every structured line.
- **No file logging exists.** `Program.cs` builds the host with default `WebApplication.CreateBuilder` (console + debug providers only). There is no `builder.Logging.AddFile(...)` / Serilog / file sink anywhere in `src/`. `~/.mohist/logs/*.log` is never created, yet `SystemInfoService` advertises `Logs = ~/.mohist/logs` (`SystemInfoService.cs:170`). The tail endpoint's `Directory.Exists(logDir)` check therefore always returns false and the endpoint answers `{ lines: [], nextCursor: null }` — indistinguishable from "file exists but has nothing new."
- **No source-aware empty state.** When the tail is empty, `LogsPage.tsx:198-203` renders a bare `"No logs available"` with no reason or expected location. A user debugging the daemon cannot tell "logging not configured" from "nothing logged yet."

Verified current state (code):

- The log directory is resolved **inline in the route handler** via `IEnvironmentVariableProvider(HOME)` (`LogsRoutes.cs:14-16`) — duplicated from `SystemInfoService.ResolvePaths()` (`SystemInfoService.cs:157-160`) and not overridable by config. The integration fixture already injects a `MockEnvironmentVariableProvider` and config keys (`MohistIntegrationFixture.cs:118-148`), so the seam to redirect the log directory exists but is not used for logs.
- The existing read loop is a byte-offset tail: `stream.Seek(cursor, Begin)` then `ReadLineAsync` until line-count (`limit ?? 100`) or byte (`maxBytes ?? 64KB`) cap (`LogsRoutes.cs:32-54`). `nextCursor = stream.Position`; `null` when EOF reached. This is sound for a growing append-only file and is preserved.
- Serialization goes through the shared `JSON.Options` (`Infrastructure/JSON.cs`) — `JsonSerializerDefaults.Web` (camelCase), `PropertyNameCaseInsensitive = true`, registered on the minimal API JSON pipeline in `MohistServiceRegistration.cs:134-137`. Any response type the endpoint returns is camelCased automatically.
- `IFileSystem` and `IEnvironmentVariableProvider` abstractions already exist and are registered (`MohistServiceRegistration.cs:92-93`), used by `SystemInfoService`, `ConfigService`, etc. They are the natural seam for test redirection.
- There is **no existing server spec** for `/api/logs/tail` (no `LogsRouteSpecs.cs`) and **no Web tests** under `pages/logs/`.

Constraints / stakeholders:

- Per `design/architecture.md`, the server is the control plane and owns its own operational facts; daemon log capture is a server-internal concern, not a runner concern. Runner logs are explicitly out of scope.
- Per `design/testing.md`: specs verify product behavior at the API boundary via `MohistIntegrationFixture`; no real filesystem/time/network; the file-logger directory and the tail source must be redirectable in tests without touching real `~/.mohist/logs`.
- Per `AGENTS.md`: data models stay minimal; the project is in active development with no version-compatibility obligation, so a single coordinated breaking change to `/api/logs/tail` is acceptable (no dual-read rollout).
- `JSON` line format must be one JSON object per physical line (NDJSON) so the tail can `ReadLineAsync` and parse each line independently.

## Goals / Non-Goals

**Goals:**

- One typed `/api/logs/tail` response shape both sides honor: a per-line structured element type, an incremental cursor, source identity, truncation/reset metadata, and an explicit source-unavailable state (with expected location) — distinct from an available source that returned zero new lines.
- A server file-logging provider that writes structured NDJSON records to `~/.mohist/logs/server.log` (the advertised `SystemPaths.Logs`), creating the directory if absent, in the exact element shape the tail returns.
- A source-aware Logs page: real `File:` line from source identity; an actionable unavailable diagnostic (expected location + reason) replacing the bare `"No logs available"`; level filtering, search, export, and auto-follow operating on the agreed element type with no double-parsing.
- Contract-boundary tests on both sides covering: the unavailable/missing-directory path, the agreed response shape, and a populated tailing path (incremental cursor, truncation, reset).

**Non-Goals** (per proposal/specs):

- Task execution logs (`TaskLogRoutes`, `design/task-log.md`) — separate system.
- Runner logs; workflow events as a Logs-page source (already removed).
- Log rotation, retention, size-bounding, and compression policy — out of scope; the file grows unbounded for now (noted as a follow-up). The truncation cap is per-request, not a file-size policy.
- Settings > System log-path diagnostics (#19, done) — untouched.
- Real-time push (SignalR/WebSockets) for log streaming; polling + incremental cursor remains the tail model.

## Decisions

### D1. Single typed response shape with an `unavailable` discriminator — not a tagged union.

`GET /api/logs/tail` always returns HTTP 200 with one object:

```
{
  "lines":      LogEntry[],          // present always; [] when unavailable or no new lines
  "cursor":     long | null,         // byte offset for next read; null at EOF or unavailable
  "nextCursor": long | null,         // alias kept for clarity; client stores it as cursor
  "source":     string | null,       // file path/name the lines came from; null when unavailable
  "truncated":  bool,                // true if line/byte cap hit before EOF
  "reset":      bool,                // true on first read (no cursor) or rotation/truncation
  "unavailable":    bool,            // true when the log source does not exist
  "expectedLocation": string | null, // the advertised log dir/file path, populated when unavailable
  "reason":         string | null    // human-readable reason, populated when unavailable
}
```

`LogEntry` is the single per-line element type, end-to-end:

```
{ "level": "INFO" | null, "time": "ISO8601" | null, "service": "Mohist.Server" | null,
  "message": "...", "raw": "original serialized line" }
```

Rationale: always-present fields mean the Web client never sees `undefined` for the columns it renders; the `unavailable` boolean discriminates the two empty cases (source missing vs. source present but nothing new) without requiring the client to branch on shape. `raw` is kept on every element so search/export can operate on the faithful original line even when structured fields are absent.

Alternatives considered:

- **Tagged union (`status: "available" | "unavailable"` with disjoint payloads)** — rejected: forces the client to narrow the type at every call site for a single distinction, and the spec requires the per-line list/cursor/source/truncation to be "always carried," which a disjoint union contradicts.
- **Two separate endpoints (`/api/logs/status` + `/api/logs/tail`)** — rejected: reintroduces the exact race the current page has (state inferred from an empty list); the page needs the unavailable signal *with* every tail response so polling stays correct.

Note: the existing client already names the field `cursor` while the server names it `nextCursor`. This change picks `nextCursor` as the wire field (server authority) and renames the client's stored value to match, eliminating the alias confusion at the boundary.

### D2. Server emits a custom `FileLoggerProvider` writing NDJSON — no new external dependency.

Add a small `IFileLoggerProvider : ILoggerProvider` (and `FileLogger : ILogger`) under `src/Mohist.Server/Logging/` that:

- Appends one JSON object per line to `{logDir}/server.log` via `FileShare.ReadWrite` (so the tail reader can read concurrently).
- Serializes each record through a fixed shape matching `LogEntry`: `level` (normalized to `INFO`/`WARN`/`ERROR`/…), `time` (ISO 8601 UTC from the injected `TimeProvider`), `service` (category, top segment — e.g. `"Mohist.Server"`), `message` (formatted), plus optional exception/fields as a JSON object. `raw` is reconstructed by the tail reader, not written twice.
- Creates `{logDir}` on first write if missing (satisfies the "directory created by the provider" requirement).

Rationale:

- **Zero new packages.** The repo uses central package management (`Directory.Packages.props`) with no logging sink packages; adding Serilog (`Serilog`, `Serilog.Extensions.Hosting`, `Serilog.Sinks.File`, `Serilog.Formatting.Compact`) is four new dependencies plus a parallel logging pipeline that wraps `Microsoft.Extensions.Logging`. A custom provider is ~120 LOC, lives in the existing pipeline already wired through every `ILogger<T>` in the codebase, and gives exact control over the NDJSON shape so it matches the tail contract by construction.
- **Testability.** The provider takes the log directory (and `TimeProvider`) via constructor injection, so the integration fixture redirects it to a temp dir without touching `HOME` or real `~/.mohist`.
- **Determinism.** `System.Text.Json` with `JSON.Options` serializes the record, so the file format inherits the same camelCase/encoder settings as the API — no second format to keep in sync.

Alternatives considered:

- **Serilog + Compact sink** — rejected for the dependency/pipeline cost above; the compact JSON shape also differs slightly from `LogEntry` (uses `@t`/`@l`/`@m`), requiring a transform on read.
- **`Microsoft.Extensions.Logging` console + redirect stdout to a file** — rejected: couples log shape to console formatting and to systemd unit configuration; the file would not exist in dev/non-managed runs, reproducing today's bug.
- **Buffered/async background queue inside the provider** — deferred (see Risks). The first cut writes synchronously like the console provider does; if profiling shows contention, a channel-backed queue is a localized change behind the same `ILoggerProvider` interface.

Registration: `Program.cs` gains `builder.Logging.AddFileLogger(o => o.Directory = logPathResolver.Resolve())` before `app.Build()`. The `BuildAlternateApp` path also adds it so the OTLP-bind-failure fallback keeps logging identically.

### D3. One `ILogPathResolver` shared by the file logger, the tail endpoint, and `SystemInfoService`.

Extract the inline `Path.Combine(home, ".mohist", "logs")` computation into `ILogPathResolver` (default impl `LogPathResolver`) that:

1. Honors a `Mohist:LogsPath` config override first (test fixture sets this to a temp dir).
2. Falls back to `IEnvironmentVariableProvider(HOME)` + `.mohist/logs` — identical to today.
3. Is the single source consumed by `FileLoggerProvider`, `LogsRoutes`, and `SystemInfoService.ResolvePaths()` (`Logs` field), so the advertised path and the real path can never drift again.

Rationale: today the path is computed in two places (`LogsRoutes.cs:14-16` and `SystemInfoService.cs:160/170`) with no override seam; the integration fixture cannot redirect it without polluting `HOME`. A dedicated resolver with a config override matches the existing pattern (`MohistWorkspaceLayout.RunnerRoot*` keys, `Mohist:ArtifactStorage:Root`, `Mohist:SystemUpdate:StatePath`) already used to redirect filesystem concerns in the fixture (`MohistIntegrationFixture.cs:96-115`).

The integration fixture sets `Mohist:LogsPath` to a per-run temp dir in `ConfigureWebHost` and cleans it up in `DisposeAsync`, so server specs never touch the real `~/.mohist/logs`.

Alternatives considered:

- **Redirect via `HOME` in the `MockEnvironmentVariableProvider`** — rejected: `HOME` is already set by the fixture and is shared with `SystemInfoService`'s DB/config path computation; repointing it would relocate the whole `~/.mohist`, broadening the blast radius and risking test-to-test coupling.
- **Keep the path inline but read `Mohist:LogsPath` inside `LogsRoutes`** — rejected: leaves the duplication across the three consumers and the file logger un-wired to the same override.

### D4. Cursor semantics: byte offset; `reset` on first read and on rotation/truncation.

- `cursor` (request param) and `nextCursor` (response) are byte offsets into the active log file, identical to today's `stream.Seek(startPosition)`.
- `reset: true` when **any** of: (a) no `cursor` supplied (first read — client must replace its view); (b) the file's current length is less than the supplied cursor (the file was truncated/rotated) — in this case the read restarts from byte 0 and returns `reset: true` so the client discards stale entries.
- `truncated: true` when the read stopped because `limit` or `maxBytes` was hit before EOF (the current `line is null` vs. cap distinction at `LogsRoutes.cs:40,55`).
- The reader opens with `FileShare.ReadWrite` (already present) so it does not block the logger's appends.

Rationale: byte-offset tailing is already implemented and correct for an append-only file; this decision only formalizes the reset/truncation signals the client already expected but the server never sent. Rotation detection by "file shrank" is the cheapest correct heuristic for a single-file source and needs no separate cursor store.

Alternatives considered:

- **Line-number cursor** — rejected: requires rewriting the file on rotation and breaks under truncation; byte offset self-describes position in the current file.
- **Separate `/api/logs/status` to detect rotation** — rejected: reintroduces the race D1 avoids.

### D5. Tail reads `server.log` (the active file); source identity is the file name.

The file logger writes a single active file, `server.log`. The tail endpoint reads that file directly (the path from `ILogPathResolver` + `server.log`). Source identity returned to the client is the file name (`server.log`) — stable across restarts, sufficient for the `File:` line, and avoids leaking absolute paths to the browser.

Rationale: a single well-known active file makes rotation/retention a future, localized change (a future `server.YYYY-MM-DD.log` + symlink scheme plugs into the same `ILogPathResolver`). Reading the newest `*.log` (today's behavior) is retained only as a discovery fallback for the transitional case where `server.log` does not yet exist but an older log does.

Alternatives considered:

- **Glob the newest `*.log`** (today) — rejected as the primary path: it makes source identity non-deterministic and defeats `reset` reasoning when the active file rolls over.

### D6. Web consumes the agreed element type directly; `parseLogLine` is deleted.

- `pages/logs/model/api.ts`: `LogTailResult` is retyped to the agreed shape; a `LogEntry` type is exported.
- `pages/logs/model/useLogs.ts`: remove `parseLogLine`/`JSON.parse`; map `result.lines` straight into the entry list. Expose `source`, `unavailable`, `expectedLocation`, `reason`, `truncated`, `reset`, `nextCursor` (stored as the new cursor). `reset` triggers view replacement (today's `result.reset` branch at `useLogs.ts:69` is preserved and now actually works).
- `pages/logs/ui/LogsPage.tsx`: render `File: {source}` from the real identity; replace the bare empty state (`:198-203`) with two branches — an **unavailable diagnostic** (expected location + reason) when `unavailable`, and a neutral "no matching logs" otherwise. `LogRow` already consumes `level/time/service/message`; it now reads them from the structured element instead of the parse fallback. Export emits each entry's `raw` (faithful original line) joined by newlines, preserving the current `.txt` export behavior.
- Auto-follow polling stores `nextCursor` and passes it back; the existing `MAX_ENTRIES`/`POLL_INTERVAL`/visibility gating (`useLogs.ts:33-34,99-126`) is unchanged.

Alternatives considered:

- **Keep `parseLogLine` as a defensive re-parse** — rejected: it reintroduces the double-parse bug and contradicts the spec requirement that the client render elements without `JSON.parse`.
- **Drop `raw` from the Web model to slim the type** — rejected: export and search-across-original-text depend on it (`LogsPage.tsx:56,87`); removing it would regress both features.

## Risks / Trade-offs

- **`[Custom file logger blocks the logging thread on synchronous append]`** → The console provider is already synchronous on the calling thread; the file provider is no worse. Mitigation: open the file once in append mode with a buffered `StreamWriter` over a `FileStream(FileShare.ReadWrite)` and flush on write; if profiling under heavy logging shows contention, swap the write path for a bounded `Channel`-backed queue behind the same `ILoggerProvider` interface (localized change, no contract impact).
- **`[Logger and tail contend on one file]`** → Both open with `FileShare.ReadWrite`; the logger appends, the reader seeks+cursors. Mitigation: the reader never holds the file open between requests (stateless cursor), so contention is per-request-read vs. per-log-line-write, both short.
- **`[Unbounded log file growth]`** → Out of scope per Non-Goals; the file has no rotation/retention. Mitigation: documented as a follow-up issue; `truncated` bounds **per-request** read volume, not file size, so the page stays responsive even as the file grows.
- **`[Rotation heuristic ("file shrank") false-positives if the file is briefly empty]`** → Truncation to length 0 between polls reads as `cursor > length` → `reset`. This is correct (the client should replace stale entries) and cheap; the only cost is a one-time view reset on real rotation, which is the intended behavior.
- **`[Breaking the existing `/api/logs/tail` wire shape with no version gate]`** → The page is the only known consumer (no runner/CLI usage found in `src/`). Mitigation: coordinated server+Web change in one PR; acceptance test at the API boundary (`LogsRouteSpecs`) locks the new shape so a future drift is caught.
- **`[Custom logger shape divergence from `LogEntry` over time]`** → Both serialize via `JSON.Options` and a shared record type (`LogRecord`) that the logger writes and the tail reads into `LogEntry`. Mitigation: a unit test asserts a written line round-trips through the tail's `LogEntry` projection without field loss.

## Migration Plan

No DB/config migration is required (`~/.mohist/logs` is created by the provider on first write; `SystemInfoService` advertises the same path).

Deploy steps (single coordinated PR):

1. **Server first, within the same build:**
   - Add `ILogPathResolver` + `LogPathResolver`; rewire `SystemInfoService` and `LogsRoutes` to consume it.
   - Add `FileLoggerProvider` under `src/Mohist.Server/Logging/`; register in `Program.cs` (both the primary and `BuildAlternateApp` code paths).
   - Rewrite `LogsRoutes` to the agreed shape (D1), with reset/truncation/unavailable semantics (D4) and `server.log` as the source (D5).
   - Add `LogsRouteSpecs` at the API boundary: missing-directory/unavailable, agreed shape, populated tail (incremental cursor, truncation, reset).
2. **Web, same PR:**
   - Retype `LogTailResult`/`LogEntry`; delete `parseLogLine`; wire `useLogs` to the new fields; render the unavailable diagnostic in `LogsPage`.
   - Add Web unit tests for `useLogs` (element type, `reset` replace-vs-append, unavailable passthrough) and `LogsPage` (unavailable diagnostic vs. filtered-empty).
3. **Verify:** `npm test` (server), `npm run test:run -w packages/web` and `npm run typecheck -w packages/web`.

Rollback strategy: revert the single PR. Because the page is non-functional today, rolling back returns it to the known-broken state — no data or persistence shape depends on the change (logs are ephemeral operational files; nothing reads them except this page). No forward-compatibility shim is warranted given the no-version-compatibility project stance.

## Open Questions

- **Log record enrichment.** Should the file logger capture structured fields already attached to `ILogger` scopes / named properties (e.g. `IssueNumber`, `WorkflowRunId`), or only the formatted message + level/time/service? The minimal shape in D2 carries only the latter; enriching the record would let the Web filter/search by structured field but grows the element type. **Default: minimal shape now; revisit if the Logs page needs structured filtering.**
- **Should the unavailable `reason` be a free-form string or a small enum (`directory_missing` / `file_missing` / `read_error`)?** Free-form is simpler for the first cut and renders directly; an enum would let the Web map to per-cause copy. **Default: free-form string for now; promote to enum if a second cause needs distinct UI.**
- **`server.log` vs date-stamped files from day one?** D5 picks a single active file. If a daily-rotation scheme is wanted soon, landing `server.log` now means a future migration renames the active file — cheap, but worth deciding before external tooling (log shippers) depends on the filename. **Default: single file now; rotation is an explicit Non-Goal.**
