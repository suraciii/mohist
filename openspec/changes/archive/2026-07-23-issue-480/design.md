## Context

The `mo` CLI reaches three distinct objects through overlapping verbs:

- **Server-registered Runner resources** (remote): `runner list`/`show`/`status` query `GET /api/projects/<project>/runners[/<id>]`.
- **The connected Mohist Server application** (remote): `server health` (`/api/health`), `server info` (`/api/system/info`), plus `project status` (`/api/status?all=true`) and `system logs` (`/api/logs/tail`) which are Server-application facts parked under the wrong groups.
- **Local OS-managed processes** (local): `server start/stop/restart/status/logs/uninstall` and `runner start/stop/restart/service-status/logs/uninstall` all route through `IServiceInstaller` (systemd unit / Windows scheduled task).

Today `ServerCommands` and `RunnerCommands` each wire both remote reads and local lifecycle into the same command group (`MohistCliCommands.Server.cs`), so a `status` or `logs` verb's target object is ambiguous. Issue #387 already moved root `mo status`→`project status` and `mo logs`→`system logs`, and `system info`→`server info`; #480 completes the separation to `runner` (resource) / `server` (application) / `service` (local process).

Constraints: this is a CLI-only change (issue risk = medium; "does not change Runner scheduling or Server domain state"). The repo is in active development with no version-compatibility constraint, so breaking the command tree is acceptable. `IServiceInstaller` has non-CLI callers (the update flow: `UpdateOperations`/`Update.Stages` call `StopRunnerAsync`/`StartRunnerAsync`; `RunnerRefreshVerifier`) that must keep working.

## Goals / Non-Goals

**Goals:**
- Each command group addresses exactly one object: `runner` = remote resource, `server` = connected application, `service` = local process.
- One canonical entry per lifecycle verb, with honest, non-interchangeable log sources (app vs service-manager) and no `--source` merge.
- Keep `IServiceInstaller` and its programmatic callers intact; move only CLI entry points.

**Non-Goals:**
- No Server API, route, Runner, Web, or database change.
- No alias/shim for removed verbs; no `--source` flag.
- No new Runner resource-control actions (drain/pause/remote-restart).
- No change to OS service-manager support or install media.

## Decisions

### D1 — Reuse `IServiceInstaller` unchanged; only CLI entry points move
The new `ServiceCommands` maps each `(verb, target)` pair to the existing per-target installer method via a small dispatch table (`start`+`server`→`StartServerAsync`, … `uninstall`+`runner`→`UninstallRunnerAsync`), reusing `ServiceCommandOptions`/`ServiceInstallOptions`. The interface, `SystemdServiceInstaller`, and `WindowsScheduledTaskInstaller` are untouched, so the update flow and runtime-consistency callers keep working.
- **Alternative:** generalize the interface to `ExecuteAsync(ServiceAction, ServiceTarget, ServiceCommandOptions)`. Rejected — larger blast radius across non-CLI internal callers, and the per-target methods already encode unit-name resolution cleanly inside each installer.

### D2 — `target` as an enum positional argument for parse-time validation
Define `ServiceTarget { Server, Runner }` and bind the positional `<target>` to it. Unknown values become System.CommandLine parse errors → exit `2` (usage failure), satisfying the spec's "usage error" with no hand-written validation, and the allowed values are auto-discoverable in `--help`.
- **Alternative:** `Argument<string>` + manual handler validation returning exit `1` (how `runner list --scope` validates today). Rejected — a fixed target vocabulary is a true usage error; the enum yields correct exit-2 semantics for free and avoids duplicating the validation per verb.

### D3 — `server status`/`server logs` reuse existing API paths (no endpoint change)
`server status` issues the same `GET /api/status?all=true` that `project status` used; `server logs` issues the same `GET /api/logs/tail` that `system logs` used. Both are pure relocations of which command issues the request — no Server route or response-shape change. `server health`/`info` are unchanged.
- This bounds the change to the CLI and keeps risk at medium.

### D4 — Remove the `system` group entirely
`system`'s only remaining member is `logs` (application logs); `info` already moved to `server info` (#387). Once `logs` becomes `server logs`, the group is empty and is removed, honoring the "one canonical entry per capability" principle and the issue's no-alias rule. The disambiguation text currently in `SystemCommands` (app logs vs service-manager logs vs `mo info`) relocates into the `server logs` and `service logs` command descriptions.
- **Alternative:** keep `system logs` as a hidden alias. Rejected — the issue explicitly forbids retained aliases for relocated paths.

### D5 — Rename `runner show` → `runner view`
Aligns the single-resource read verb with the CLI-wide unification target (`view`/`edit`, per the `docs/cli-reference.md` gap list) and the issue's `list/view/status` wording. `show` is removed with no alias.
- **Alternative:** defer to a separate verb-unification issue. Rejected — the AC names `view` and the `runner` group is already being rewritten here, making this the natural moment.

### D6 — Update cross-cutting "start with" guidance strings
Several user-facing strings hardcode verbs that no longer exist: `MohistCliApi.ServerUnavailableMessage` ("Start with: mo server start", used by every remote read on server-down), the `system info` degraded message (`MohistCliApi.cs:729`), `NdjsonStream.cs:30`, and `Update.Finalize.cs:66` ("Start the runner manually with: mo runner start"). These are updated to `mo service start server` / `mo service start runner`. Centralizing the message in the existing `ServerUnavailableMessage` constant keeps it to one source of truth.
- Trade-off: this changes stderr text emitted by many commands on server-down. Acceptable because the new text is strictly more correct; tests asserting the old literal must update.

### D7 — `install`/`update` stay root-level; not duplicated under `service`
`install server`/`install runner` (`InstallCommands`) and `update server`/`update runner` (`UpdateCommands`) remain root-level. `service` exposes only the six lifecycle verbs (`start`/`stop`/`restart`/`status`/`logs`/`uninstall`).

## Risks / Trade-offs

- [Breaking command tree] Scripts invoking `mo server start`, `mo runner logs`, `mo project status`, or `mo system logs` fail hard (exit `2`, no alias). -> Mitigation: intended, documented break; ships in an active-dev period with no version-compat constraint; docs updated in the same change.
- [Cross-cutting string change touches many commands' stderr] -> Mitigation: centralize in `ServerUnavailableMessage`; update the few tests asserting the old literal.
- [Update flow depends on `IServiceInstaller`] -> Mitigation: D1 keeps the interface intact, so update/runtime-consistency callers are unaffected.
- [`server status`/`server logs` meaning flips] `server status` meant "local unit status", `server logs` meant "service-manager logs"; both now mean application-side facts. -> Mitigation: help text and the `service logs`/`service status` counterparts make the object explicit; the spec's non-interchangeability scenarios guard the boundary.

## Migration Plan

- Single CLI-only change; no Server/Runner/Web coordination, no data or remote state to migrate.
- Code: split `MohistCliCommands.Server.cs` (drop lifecycle verbs from `ServerCommands`/`RunnerCommands`, rename `show`→`view`); add `ServiceCommands` with enum target + dispatch table; remove `project status` from `MohistCliCommands.Project.cs`; remove `SystemCommands` and its root registration in `MohistCliCommands.cs`; register `service`; update the D6 guidance strings.
- Tests: rewrite `CliRunnerCommandSpecs.cs`, `CliProjectStatusCommandSpecs.cs`, `CliSystemLogsCommandSpecs.cs` to the new paths; add `CliServiceCommandSpecs` covering target validation, log-source separation, no-remote-mutation, no-`--project`, dry-run, and removal of the old verbs.
- Docs: `docs/cli-reference.md` is already target-shaped (its gap entry closes here); update examples in `docs/runner.md`, `docs/concepts.md`, `docs/troubleshooting.md`, `docs/issues.md`.
- Rollback: revert the CLI commit; nothing else to unwind.

## Open Questions

- Should `UpdateOperations.RestartCommandLine` (currently raw `systemctl --user restart …` hints) also surface `mo service restart <target>`? Optional polish, not required by any spec; defer unless it causes confusion.
