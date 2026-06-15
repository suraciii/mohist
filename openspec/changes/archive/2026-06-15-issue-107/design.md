## Context

The `mo info` command is a new CLI subcommand that provides a one-glance environment and installation source overview. It answers "which code is installed, which directory is each service running from, and are versions consistent?"

The CLI already uses `System.CommandLine` with command builder classes (`ServerCommands`, `RunnerCommands`, `ProjectCommands`, etc.) registered via `MohistCliCommands.Build()`. The `SystemdServiceInstaller` knows unit names (`mohist.service`, `mohist-runner.service`) and already invokes systemctl. The server has `SystemdUnitParser` for parsing unit file content and `GitSourceInspector` for git state — these are server-side classes, but similar logic is needed in the CLI for local data gathering.

The CLI communicates with the server via `MohistCliApi` (wraps `HttpClient`). External commands run through `ICommandExecutor`. Project state is persisted in `~/.mohist/cli-state.json`.

## Goals / Non-Goals

**Goals:**
- Add `mo info` as a top-level command producing <=10 lines of default output in <1s
- Support `--verbose` for detailed fields and `--json` for machine-readable output
- Fail-safe: each data source failure is independent, never blocks other fields
- Cross-platform: full systemd details on Linux, graceful degradation on macOS/Windows
- Reuse existing abstractions: `ICommandExecutor`, `MohistCliApi`, `System.CommandLine` patterns

**Non-Goals:**
- No active health diagnosis or warnings (that's `mo doctor`, a future issue)
- No modification of systemd service files
- No `--watch` real-time refresh
- No remote instance inspection (local only)
- No changes to server API or data model

## Decisions

### 1. New `InfoCommands` class following existing CLI pattern

Add a static `InfoCommands` class (alongside `ServerCommands`, `RunnerCommands`) that builds the `mo info` command via `System.CommandLine`. A new `InfoCollector` service class aggregates all data sources.

**Rationale**: The existing command builder pattern (`static class XyzCommands { public static Command Build(...) }`) is consistent and minimal. No new abstractions or frameworks needed.

**Alternatives considered**: Adding info as a subcommand under `server` or `project` — rejected because it's a top-level cross-cutting concern, not scoped to a single subsystem.

### 2. Local-first data collection, not server API extension

Collect all information from local sources: systemctl, procfs, git, filesystem, environment variables. Only use the existing server API for project/issue counts (`/api/projects`) and connectivity verification.

**Rationale**: The command's value is showing what's running *locally*. If the server is broken or running wrong code, we still want to report that. Embedding this as a server endpoint would fail when the server is misconfigured — the exact scenario that motivated this issue.

**Alternatives considered**: Adding a `/api/info` server endpoint and having the CLI proxy to it — rejected because it creates a circular dependency (can't diagnose server problems if the server must be working).

### 3. Data collection via individual collector methods with independent error handling

Each field (CLI version, server status, server source, server git, runner status, runner source, runner git, connectivity, project info, data dir) is collected by a distinct async method. Each method catches its own exceptions and returns a sentinel value (`<unknown>`, `<not running>`, `<not installed>`) on failure.

```csharp
// Pseudocode for InfoCollector pattern
async Task<ServiceStatus> GetServerStatus() {
    try {
        var output = await executor.ExecuteAsync("systemctl", ["--user", "show", "mo-server.service", "-p", "ActiveState,MainPID,ExecMainStartTimestamp"]);
        return ParseServiceStatus(output);
    } catch { return ServiceStatus.Unknown; }
}
```

**Rationale**: Fail-safe is a first-class requirement. Independent collection means a git failure doesn't block uptime display. Simple try/catch per method is cleaner than a generic fallback wrapper.

**Alternatives considered**: A unified "collect all" method with `AggregateException` handling — rejected because it blurs which field failed and makes sentinel value decisions harder.

### 4. Source path resolution from systemd unit `WorkingDirectory`

Parse the unit file using logic similar to the server's `SystemdUnitParser`:
1. Get unit file path from `systemctl --user show <unit> -p FragmentPath`
2. Read the unit file and extract `WorkingDirectory`
3. Fall back to parsing `--project <path>` from `ExecStart`
4. Fall back to the directory of the binary path in `ExecStart`

**Rationale**: `WorkingDirectory` is the authoritative source (services are launched from there). `ExecStart` contains the relative project path. The server already has `SystemdUnitParser` doing the same parsing — the CLI should implement equivalent logic, not import the server library.

**Alternatives considered**: Only reading `WorkingDirectory` — rejected because the unit file might not have one if it was hand-edited. Reading /proc PID cwd — rejected because it only works for running services and may show a subdirectory.

### 5. Timeout per collector, not overall timeout

Each collector method has an independent 1-2s timeout via `CancellationTokenSource`. Default-mode collectors that take too long return `<timeout>` and the command continues printing remaining fields.

**Rationale**: The spec requires <1s target but also requires "a slow data source does not block output." Per-collector timeouts with parallel execution via `Task.WhenAll` let fast fields render while slow ones time out.

**Alternatives considered**: Single global timeout via `CancellationToken` — rejected because a single slow collector would kill all remaining collectors. Sequential collection — rejected because it can't hit <1s even with all sources fast.

### 6. Output rendering via direct console formatting

Default and `--verbose` output uses direct `TextWriter` formatting (no `Spectre.Console` or similar). `--json` output uses `System.Text.Json.JsonSerializer`.

**Rationale**: The output is simple key-value lines — no tables, no colors needed for this command. Avoiding external dependencies keeps the CLI binary small and startup fast.

**Alternatives considered**: Using the existing `TableRenderer` or `Spectre.Console` — rejected because the output is line-oriented, not tabular, and these add startup overhead for the <1s target.

### 7. Connectivity check via existing `/api/projects` endpoint

The runner-to-server connectivity check makes a single HTTP GET to `/api/projects` (the same endpoint used by `mo project list`). A 2xx response = `server ok`, anything else = `server unreachable`. The server URL is derived from the runner's `SERVER_URL` environment variable in its systemd unit, defaulting to `http://127.0.0.1:3456`.

**Rationale**: This endpoint is always available, lightweight, and doesn't need auth. It validates the runner's ability to reach the server without adding new server endpoints.

**Alternatives considered**: Adding a `/api/health` endpoint just for this — rejected because `/api/projects` already exists and the server's `/api/health` is already used by `mo server health`. Creating a dedicated `/api/ping` — unnecessary.

## Risks / Trade-offs

**[Risk] systemctl parsing is fragile across systemd versions** → Use `systemctl --user show <unit> -p <property>` key-value output, which is stable and explicitly designed for programmatic consumption. Avoid parsing `systemctl status` human-readable output.

**[Risk] Git operations may be slow on large repos** → Use `git -C <path> rev-parse --short HEAD` (O(1) operation) combined with `git log -1 --format=%s`. Avoid `git status`, `git diff`, or any operation that scans the working tree. Set a 2s timeout per `Process.WaitForExitAsync`.

**[Risk] Parallel data collection may produce inconsistent snapshot** → Acceptable. The command is a point-in-time overview, not a transactional snapshot. Fields like uptime and disk usage are inherently time-varying.

**[Risk] `--json` output may change over time** → Document JSON field stability in the spec. Add new fields under `--verbose` only. Reserve the right to add top-level keys but never remove or rename existing ones without a breaking-change notice.

**[Trade-off] CLI binary embeds git/systemctl knowledge** → The CLI already invokes systemctl (via `SystemdServiceInstaller`) and git (implicitly via source code update flows). This command consolidates that knowledge rather than introducing a new dependency direction.

## Migration Plan

- **Deployment**: New command is purely additive. Ship as part of the next CLI build. No database migrations, no server changes, no configuration changes.
- **Rollback**: If the command has issues, it can be removed from `MohistCliCommands.Build()` with no impact on other commands. No state is written.
- **Testing**: Unit test individual collectors with mocked `ICommandExecutor`. Integration test on Linux with real systemd services. Smoke test on macOS to verify graceful degradation.

## Open Questions

1. **Exact server URL for connectivity check**: Should it come from `SERVER_URL` env var in the runner's systemd unit file, or from a well-known default? The issue suggests parsing the runner unit's environment — this is the more robust approach but adds implementation complexity.

2. **Skills versioning in `--verbose`**: The issue mentions "skills list: name + version + install path." Mohist built-in skills don't currently have version metadata. Should this field use the skill directory path as a proxy, or should we add version metadata to the skill manifest?

3. **Data directory size calculation**: Computing directory size (`du -sh`) can be slow on large directories. Should this use a cached value, run async in the background, or accept it may exceed the <1s target and fall back to `<unknown>`?
