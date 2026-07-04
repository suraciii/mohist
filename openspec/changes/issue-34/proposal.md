## Why

The `/logs` page is the system-level log viewer for diagnosing server daemon
behavior, but it is broken end-to-end today. The server and Web disagree on the
`/api/logs/tail` response shape, so every UI field except `lines` is `undefined`
and the page never actually works; the server has no file-logging provider at all,
so `~/.mohist/logs/*.log` is never created and the tail always returns empty; and
when logs are unavailable the page shows a bare "No logs available" with no reason
or expected location — leaving a user debugging a server problem unable to tell
whether logs are genuinely empty or simply not being captured. The `Logs` path
`SystemInfoService` advertises (`SystemInfoService.cs:170`) points at a directory
that does not exist.

## What Changes

- **BREAKING** (API): Replace the ad-hoc `/api/logs/tail` response with a single
  typed contract both sides honor — a consistent per-line element type, an
  incremental cursor, source identity (which file/source), and truncation/reset
  metadata. The current server shape `{ lines: object[], nextCursor }`
  (`LogsRoutes.cs:57-61`) and the client shape
  `{ file, cursor, lines: string[], truncated, reset }` (`api.ts:3-9`) both move
  to the agreed shape.
- Fix the double-parse: the server emits already-deserialized JSON objects as
  `lines` (`LogsRoutes.cs:45-51`) while `useLogs.parseLogLine` runs `JSON.parse`
  on each (`useLogs.ts:12-14`), so level/time/service/message extraction silently
  fails. Agree on one element type the Web renders without re-parsing.
- Wire a real file-logging provider so the server daemon writes structured (JSON)
  logs to `~/.mohist/logs/*.log` — the path `SystemInfoService` already advertises
  — making runtime logs actually exist instead of always empty.
- Add an explicit source-unavailable state to `/api/logs/tail` (with the expected
  log location) for the case where the log directory/file is absent, distinct from
  "tail returned zero new lines."
- Make the Logs page source-aware: when runtime logs are unavailable, render an
  actionable diagnostic (expected location + reason) instead of a bare "No logs
  available"; keep the `File:` source line, level filtering, search, export, and
  auto-follow operating against the real agreed source identity.

Out of scope: task execution logs (`TaskLogRoutes`, `design/task-log.md`); runner
logs; workflow events as a Logs-page source (already removed).

## Capabilities

- `logs-tail-api`: The `/api/logs/tail` contract — the single typed response shape
  (per-line element type, incremental cursor, source identity, truncation/reset),
  the explicit source-unavailable state with expected location when the log source
  does not exist, and populated tailing (incremental cursor, truncation).
  Server-implemented; covered at the API boundary.
- `server-file-logging`: The server daemon writes structured (JSON) file logs to
  the advertised `~/.mohist/logs/*.log` location in the format the tail API and Web
  agree on, so the advertised `SystemPaths.Logs` is truthful and the Logs page has
  real content.
- `logs-page`: The Web `/logs` page source-aware presentation — rendering the
  source (`File:` line), the actionable unavailable/empty state (expected location
  + reason, not a bare "No logs available"), and that level filtering, search,
  export, and auto-follow consume the agreed line element type without
  double-parsing.

## Impact

- **Server (C#)**:
  - `Api/LogsRoutes.cs` — rewrite `/api/logs/tail` to return the agreed typed
    response, emit a consistent per-line element, surface source identity +
    truncation/reset, and return an explicit unavailable state when
    `~/.mohist/logs` or its log file is absent.
  - `Program.cs` — add a file-logging provider writing structured JSON logs to
    `~/.mohist/logs/*.log` (currently only default/console logging; no provider
    present).
  - `SystemInfo/SystemInfoService.cs:170` — no logic change; its advertised
    `Logs` path becomes truthful once file logging is wired.
- **Web (React/TS)**:
  - `pages/logs/model/api.ts` — `LogTailResult` retyped to the agreed contract.
  - `pages/logs/model/useLogs.ts` — drop the `JSON.parse` double-parse
    (`parseLogLine`), consume the agreed per-line element type directly, and
    expose a source-unavailable state.
  - `pages/logs/ui/LogsPage.tsx` — render the source line against the real source
    identity; add an actionable unavailable/empty diagnostic (expected location +
    reason).
- **Tests**: add server specs at the API boundary for the contract shape, the
  no-log-file/unavailable path, and a populated-log tailing path (incremental
  cursor, truncation); add Web tests for `useLogs`/`LogsPage` covering the agreed
  element type, source line, and the unavailable diagnostic.
- **Dependencies**: a new logging provider package on the server side (exact
  choice TBD in design).
- **No DB/config migration required**; `~/.mohist/logs` is created by the
  file-logging provider.
