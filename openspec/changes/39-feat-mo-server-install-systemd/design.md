## Context

`mo server start` currently manages a daemon via `spawn({ detached: true })` + PID file at `~/.mohist/server.pid` (`packages/cli/src/cli/commands/server.ts`). This provides no auto-restart on crash, no reboot survival, and no OS integration. The server entry point (`packages/cli/src/server/index.ts`) already supports `--print-logs` (writes logs to stderr via `Log.init({ print: true })`) and has SIGTERM/SIGINT handlers that call `agentRunner.shutdown()` + `server.stop()`.

The existing CLI router is in `packages/cli/src/cli/index.ts` using `commander`. Server subcommands are defined inline. The server binary is `packages/cli/bin/mo-server` which loads `dist/server/index.js`.

Key files to modify:
- `packages/cli/src/cli/commands/server.ts` — add install/uninstall/restart/update + systemctl delegation
- `packages/cli/src/cli/index.ts` — register new subcommands
- `packages/cli/src/server/index.ts` — add `--print-logs` PID-skip logic (already partially done)

## Goals / Non-Goals

**Goals:**
- Install/uninstall mohist as a systemd user service with a single command
- Auto-detect source vs npm global install mode for correct ExecStart path
- Delegate start/stop/status/restart to systemctl when service is installed
- Handle headless SSH environments gracefully
- Provide `mo server update` for source-mode rebuild + restart

**Non-Goals:**
- macOS launchd / Windows schtasks support
- Multi-profile or EnvironmentFile support
- journald native protocol (use stderr capture)
- `mo server update` doing git pull / npm install

## Decisions

### D1: New module `packages/cli/src/cli/commands/server-systemd.ts`

Extract all systemd logic into a separate file from `server.ts`. This keeps the existing spawn-based code untouched and makes the systemd code independently testable. `server.ts` imports and calls into it for delegation.

**Functions:**
- `isSystemdServiceInstalled(): boolean` — checks `~/.config/systemd/user/mohist.service` existence
- `generateServiceFile(nodePath: string, scriptPath: string, workingDir?: string): string` — builds .service content with CR/LF validation
- `installSystemdService(): Promise<void>` — full install flow (detect mode → generate → write → daemon-reload → enable → start → linger)
- `uninstallSystemdService(): Promise<void>` — disable --now → delete → daemon-reload
- `getSystemdStatus(): Promise<SystemStatus | null>` — `systemctl show` for PID/state
- `restartSystemdService(): Promise<void>` — `systemctl --user restart`
- `detectInstallMode(): { nodePath: string, scriptPath: string, workingDir?: string }` — resolve paths

**Alternatives considered:** A separate `services/systemd-service.ts` in the services layer — rejected because this is CLI-only (runs `systemctl`, writes files), not business logic.

### D2: Install mode detection via `__dirname` traversal

Detect source mode by checking if `path.resolve(__dirname, '..', '..', 'packages', 'cli')` exists from the compiled `dist/cli/commands/` location. More precisely: from `dist/cli/commands/server-systemd.js`, walk up to find a directory containing `packages/cli/bin/mo-server`. If found, it's source mode with `WorkingDirectory` set to repo root. Otherwise, assume npm global and resolve via `npm root -g` or `process.execPath` heuristics.

Node binary path is always `process.execPath` (handles nvm/fnm/volta transparently).

**Alternatives considered:** Checking `process.env._` or `which node` — rejected because `process.execPath` is reliable and available without shelling out.

### D3: Headless SSH handling via try-catch + retry

Execute `systemctl --user` commands. If the command fails with a D-Bus / "No session for user" error, retry with `systemctl --machine <username>@ --user`. Detect SSH via `process.env.SSH_CONNECTION` and display a note.

This follows the openclaw pattern but simplified: no persistent state about headless mode, just retry on failure.

**Alternatives considered:** Pre-checking session existence via `loginctl` — adds complexity for a rare edge case. Try-catch is simpler.

### D4: Service file path constant

Service file lives at `~/.config/systemd/user/mohist.service`. The service name is hardcoded as `mohist.service`. No multi-profile or custom names.

### D5: start/stop/status delegation pattern

Add a `isSystemdServiceInstalled()` check at the top of existing `startServer()`, `stopServer()`, `serverStatus()` functions. When systemd is installed, these functions delegate to `systemctl --user` commands and return early. The existing spawn-based code remains as the fallback path.

```
startServer():
  if isSystemdServiceInstalled():
    execSync('systemctl --user start mohist.service')
    print "Server started (systemd)"
    return
  // ... existing spawn logic unchanged
```

### D6: Server PID file skip under systemd

In `packages/cli/src/server/index.ts`, the server currently does not write a PID file — that's done by the CLI `startServer()` function. Since systemd mode bypasses `startServer()`, no PID file is written automatically. This is correct behavior — systemd tracks the PID natively.

The `--print-logs` flag is already used in `server/index.ts:59` to enable stderr logging. No server-side changes needed for this.

### D7: `mo server update` implementation

Run two sequential `child_process.execSync('npm run build', { cwd })` calls:
1. `packages/cli/` (TypeScript → JS)
2. `packages/cli/web/` (Vite build)

Then `systemctl --user restart mohist.service`. Validate source mode first, validate systemd service exists, and abort on any build failure without restarting.

### D8: CR/LF injection prevention

Before writing the .service file, validate that all resolved paths and values contain no `\n` or `\r` characters. Quote paths with spaces using systemd's quoting rules (wrap in `"` if contains spaces). This is a simple string check, not a full systemd escaping library.

## Risks / Trade-offs

- **[systemd not available (WSL1, containers)]** → `mo server install` exits with clear error message. Existing spawn-based commands still work as fallback.
- **[Headless SSH D-Bus issues]** → Try-catch retry with `--machine` flag. May still fail in exotic setups; user can run `mo server start` instead.
- **[npm global path detection unreliable]** → Fallback: resolve mo-server relative to `process.argv[1]` or `__dirname`. Worst case: user manually edits .service file.
- **[Source mode detection false positive]** → Check for `packages/cli/bin/mo-server` specifically, not just directory existence. Makes detection robust.
- **[Build step during update fails]** → Abort without restart; service keeps running previous version. User sees build error output.

## Migration Plan

No migration needed. This is purely additive:
1. Add `server-systemd.ts` with all systemd functions
2. Add `installSystemdService`, `uninstallSystemdService`, `restartServer`, `updateServer` exports to `server.ts`
3. Register new subcommands in `cli/index.ts`
4. Add delegation checks in existing `startServer()`, `stopServer()`, `serverStatus()`

Existing behavior is preserved when systemd service is not installed — all commands follow the current spawn-based code paths.

Rollback: `mo server uninstall` restores previous behavior entirely.

## Open Questions

None. All decisions are resolved per the issue spec.
