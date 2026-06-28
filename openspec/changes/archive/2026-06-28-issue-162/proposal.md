## Why

CLI users currently cannot observe system state without switching to `curl` or the Web UI. Three frequent "look before you act" needs are unmet: discovering which coder models are available before setting `--model`, reading system diagnostics (version, git source, service status, paths), and checking which runners are online and idle. All three queries already have stable server endpoints consumed by the Web UI, but the CLI exposes no command entry point for them. This issue closes that parity gap with read-only commands.

## What Changes

- Add `mo system info` — a top-level read-only command hitting `GET /api/system/info`, rendering identity/source/install/update/services/paths. The command SHALL degrade gracefully (print the locally-available subset + a "service not running" notice) when the server is offline, rather than failing outright.
- Add `mo opencode models` — a top-level read-only command listing available coder model IDs (one per line in table mode for easy copy-paste into `--model`).
- Add a read-only runner online-status command hitting the project-scoped runner list endpoint, showing each runner's id, last heartbeat, and idle/busy state.
- Resolve the `mo runner status` naming collision: the name is already taken by the systemd service-lifecycle command (`mo runner status` shells out to `systemctl`). The exact resolution (rename the existing service-lifecycle verb to `mo runner service-status`, or name the new diagnostic differently) is deferred to the Plan phase. **Potential BREAKING** if the existing service-lifecycle verb is renamed.
- All three commands support `-o table|json`.
- No server-side changes, no write operations, no Web UI changes.

## Capabilities

### New Capabilities

(None — the new commands extend an existing capability.)

### Modified Capabilities

- `cli-interface`: Add requirements for the three read-only diagnostic commands (`mo system info`, `mo opencode models`, runner online status), including graceful-degradation behavior, output modes, and the project-scope resolution needed for the opencode-models and runner-status endpoints. Also capture the naming-collision resolution once decided in the Plan phase.

## Impact

- **Code**: `packages/cli/Mohist.Cli/` — new command files (or additions to existing group files like `MohistCliCommands.Server.cs`), new `TableShape` entries and renderer branches in `TableRenderer` partials, new API helper methods in `MohistCliApi` for the three GET paths.
- **Endpoint scope discrepancy**: The issue body describes `GET /api/opencode/models` (global) and `GET /api/runner-status` (global), but the actual server routes are **project-scoped**: `GET /api/projects/{projectRef}/opencode/models` and `GET /api/projects/{projectRef}/runners`. Both commands therefore require active-project resolution (same pattern as `mo runner list`). Only `GET /api/system/info` is truly global.
- **Overlap with existing commands**: `mo runner list` already hits the same project-scoped runner endpoint with full table rendering (id/kind/status/scope/capacity/heartbeat/hostname). The new runner-status diagnostic must differentiate from or consolidate with `mo runner list` — decided in the Plan phase.
- **Naming similarity**: `mo system info` (server-side diagnostics) vs the existing client-local `mo info` (CLI environment overview). Different data sources, but help text should disambiguate.
- **Dependencies**: No new server endpoints, no new CLI dependencies.
- **Tests**: New xUnit specs in `packages/cli/tests/Mohist.Cli.Tests/` using the `RecordingHttpHandler` + in-process `RunAsync` pattern, covering table/json rendering and graceful degradation for each command.
