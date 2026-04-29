## Why

`mo server start` uses spawn detached process + PID file for daemon management, which provides no auto-restart on crash, no survival across reboots, and no OS-level integration (logs, status). Installing as a systemd user service solves all three problems with a single `mo server install` command, while keeping the existing spawn-based flow as a fallback.

## What Changes

- **New**: `mo server install` — detect install mode (source vs npm global), generate systemd user service file, enable/start service, enable linger, handle headless SSH
- **New**: `mo server uninstall` — disable/stop service, remove .service file, daemon-reload
- **New**: `mo server restart` — `systemctl --user restart` (systemd) or stop+start (fallback)
- **New**: `mo server update` — source-mode only: build CLI + web + restart service
- **Modified**: `mo server start/stop/status` — auto-delegate to systemctl when systemd service is installed; fallback to current spawn behavior otherwise
- **Modified**: `mo server status` — prefer `systemctl show` for PID/status when systemd service exists; fallback to pidfile
- **Modified**: server process — skip PID file write under systemd; handle SIGTERM for graceful shutdown; support `--print-logs` flag to output logs to stderr for journald capture

## Capabilities

### New Capabilities

- `systemd-install` — install/uninstall mohist server as systemd user service, with path resolution, linger, and headless SSH handling
- `server-update` — source-mode rebuild + restart workflow for development iteration

### Modified Capabilities

- `server-daemon` — start/stop/status delegate to systemctl when systemd service is installed; SIGTERM handling; `--print-logs` flag; skip PID file under systemd
- `cli-interface` — new `install`, `uninstall`, `restart`, `update` subcommands under `mo server`

## Impact

- **CLI commands**: New subcommands in `packages/cli/src/cli/commands/server.ts` (or new service module)
- **Server process**: SIGTERM handler and `--print-logs` flag in `packages/cli/src/server/`
- **Generated file**: `~/.config/systemd/user/mohist.service` (user-writable, no sudo)
- **Existing behavior preserved**: When no systemd service is installed, all commands work exactly as today
- **Reference code**: `opensrc/openclaw/src/daemon/` (systemd-unit.ts, systemd.ts, program-args.ts, systemd-linger.ts) for implementation patterns
