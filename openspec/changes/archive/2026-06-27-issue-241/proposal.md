## Why

The server already exposes four per-session management endpoints under `/api/projects/{p}/issues/{n}/sessions/{name}/*` (metadata, transcript, compact, reset), but the CLI only wires the list endpoint (`mo issue sessions`). An agent running a long task cannot self-manage its own context window — it cannot compact when approaching the limit, reset when context is polluted, or read a transcript to resume/review — because its only interface (the CLI) is missing the verbs. This forces workflow or human intervention for what should be an agent-driven operation, making long autonomous runs fragile.

## What Changes

### New `mo issue session` command group (singular)

A new `session` subcommand under `mo issue`, exposing the four existing endpoints. Pure CLI wiring — no server changes. The existing `mo issue sessions <num>` (plural, list) is preserved unchanged and remains the source of the `<name>` argument used by the new verbs.

- **`mo issue session show <num> <name>`** — `GET /{n}/sessions/{name}`. Returns session metadata (created time, message count, token estimate). Supports `-o table|json`.
- **`mo issue session transcript <num> <name>`** — `GET /{n}/sessions/{name}/transcript`. In `-o table` gives a summary (message count, first/last timestamp, total token estimate); in `-o json` returns the full transcript in its raw server JSON shape.
- **`mo issue session compact <num> <name>`** — `POST /{n}/sessions/{name}/compact`. Prints the new session id (`New session: <id>`) from the recovery result so the agent/user knows the follow-on session identifier.
- **`mo issue session reset <num> <name>`** — `POST /{n}/sessions/{name}/reset`. Prints the new session id, same shape as `compact`.
- All four subcommands support `--project/--project-id` and `-o table|json`.
- `mo issue session --help` lists `show / transcript / compact / reset`.

### Error transparency for active-session conflicts

`compact` and `reset` return HTTP 409 with `code: "session_active"` when the session is currently active. The CLI SHALL surface the server's `code` and `error`/`message` to the user (via `mohist-cli-format` output or stderr) rather than treating the response as a generic failure or silently succeeding. This is the load-bearing behavioral requirement, since an agent that believes a compact succeeded when it was rejected will keep operating on a polluted/full context.

## Capabilities

### New Capabilities

_None._ The underlying server endpoints and domain behaviors already exist; this change wires them to the CLI.

### Modified Capabilities

- `cli-interface`: the `mo issue` command group gains a `session` (singular) subcommand group with `show` / `transcript` / `compact` / `reset` verbs that drive the existing per-session endpoints. Adds the `<name>` positional argument (sourced from `mo issue sessions <num>`), `-o table|json` output modes (including a table-mode summary for the potentially long transcript output, and `New session: <id>` reporting for compact/reset), `--project`-ref support, and 409 `session_active` error-code passthrough for the mutating verbs.

## Impact

- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs`): add a `BuildSession` command group with four children, routing through `MohistCliApi`. The mutating verbs need POST support plus non-2xx body parsing to extract `code`/`error` on conflict.
- **CLI** (`packages/cli/Mohist.Cli/MohistCliApi.cs`): ensure the API layer can issue `POST` requests to the compact/reset endpoints and surface the structured error (`code: "session_active"`) rather than collapsing non-success into a bare status code; the existing `ApiResponseException(message, code)` path is the intended vehicle.
- **CLI** (`packages/cli/Mohist.Cli/TableRenderer.*.cs`): add table renderers for the session metadata shape, the transcript summary (message count + first/last time + token estimate), and the compact/reset recovery result (`agentSessionId`, context-window before/after).
- **Server** (`packages/server/`): no changes. All four endpoints and the `session_active` conflict semantics already exist in `IssueRoutes.Sessions.cs`.
- **API consumers**: unchanged. The CLI becomes a first-class client of endpoints that were already public.
- **Tests**: CLI integration tests for each subcommand — success path per the acceptance criteria, plus active-session conflict error passthrough for `compact`/`reset`. No new server tests.
- **Not affected**: `mo issue sessions <num>` (list, preserved as-is), `mo issue coder-sessions` alias, attachment/metrics CLIs, session creation/deletion (sessions are workflow-managed), server domain model.
