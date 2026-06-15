## Why

Mohist services (server, runner) can silently drift to running from the wrong source directory — a recent incident saw both services' WorkingDirectory migrate to an old issue's worktree, causing every session ensure to return 404 with no indication that the root cause was a path mismatch. There is currently no single command that answers "which code is installed, which directory is each service running from, and are versions consistent?" — users must manually parse raw systemctl output from `mo server status` / `mo runner status` to extract source paths and commits, a process that is slow, error-prone, and hostile to both humans and agents during debugging.

## What Changes

- Add a new `mo info` command as the product entry point for environment and installation source overview
- Default output (<=10 lines) shows CLI version, server/runner service status with PID and uptime, resolved source directory with git commit, runner-to-server connectivity, active project name, and data directory path
- `--verbose` flag supplements with: installed skills list, git remote URL, opencode runtime info, `MOHIST_*` environment variables, OS/arch/.NET/Node versions, runner active capacity, and disk usage breakdown
- `--json` flag outputs the same information as a machine-readable nested JSON structure for external skill consumption (e.g., `mohist-explore`)
- Fail-safe design: single field read failures do not affect other fields; unavailable services show `<not running>`; non-git directories show `<not a git repo>`; non-systemd platforms degrade gracefully
- Source path resolution from systemd unit `WorkingDirectory` field, cross-validated against `ExecStart`; git info via `git -C <path> rev-parse` and `git log`

## Capabilities

### New Capabilities
- `cli-info-command`: The `mo info` command — default, verbose, and JSON output modes for environment and installation source overview, including service status, source path resolution, git commit identification, connectivity check, and cross-platform degradation

### Modified Capabilities
<!-- No existing capability requirements are changing. This is a purely additive CLI command. -->

## Impact

- Affected code: New CLI command handler in the .NET CLI project, service information gathering via systemctl parsing and procfs, git repository interrogation, and HTTP connectivity check against the server API
- Dependencies: systemd (Linux), git CLI, procfs (`/proc/<pid>/stat`), .NET `System.Diagnostics.Process`, existing server HTTP API (`/api/projects` or similar health endpoint)
- No API changes, no database schema changes, no breaking changes
