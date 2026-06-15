# OpenSpec Capability: cli-info-command

### Requirement: Default Info Output
The `mo info` command SHALL produce a concise environment and installation source overview in under 1 second. The default output MUST contain no more than 10 lines covering CLI version with binary path, server and runner service status with PID and uptime, resolved source directory with git commit, runner-to-server connectivity, active project with issue counts, and data directory path with size. Each field read failure SHALL not block output of other fields.

#### Scenario: All services healthy, consistent source
- **WHEN** both server and runner services are active, running from the same git-managed source directory, and the runner can reach the server
- **THEN** output includes: CLI version and binary path, server `active` with PID and uptime, server source path with git short SHA and commit subject, runner `active` with PID and uptime, runner source path with git short SHA and commit subject, connectivity indicator `server ok`, project name with issue counts, and data directory path with size

#### Scenario: One service not running
- **WHEN** the server service is active but the runner service is inactive
- **THEN** the server line shows status, PID, and uptime normally; the runner line shows `<not running>` in place of PID and uptime fields; remaining fields (project, data dir) still display

#### Scenario: Source directory is not a git repository
- **WHEN** a service's resolved source directory exists but contains no `.git` directory
- **THEN** the source line displays the resolved path followed by `<not a git repo>` instead of a commit SHA and subject

#### Scenario: Source directory does not exist
- **WHEN** a service's resolved source directory path does not exist on disk
- **THEN** the source line displays `<unknown>` for both path and commit information

### Requirement: Service Status via Systemd
The command SHALL resolve server and runner service status on Linux by parsing systemd user units (`mo-server.service`, `mo-runner.service`). Status fields include active state, main PID, and start timestamp. PID uptime SHALL be derived from `/proc/<pid>/stat` or `ps`.

#### Scenario: Systemd unit exists and is active
- **WHEN** `systemctl --user show mo-server.service` returns `ActiveState=active` and a valid `MainPID`
- **THEN** the server line displays `active`, the PID number, and a human-readable uptime string

#### Scenario: Systemd unit exists but is inactive
- **WHEN** `systemctl --user show mo-server.service` returns `ActiveState=inactive`
- **THEN** the server line displays `<not running>` without a PID or uptime

#### Scenario: Systemd unit not found
- **WHEN** `systemctl --user show mo-server.service` fails because the unit does not exist
- **THEN** the server line displays `<not installed>` and does not error or block other fields

#### Scenario: Systemd is not available (macOS/Windows)
- **WHEN** the `systemctl` command is not found or fails to execute
- **THEN** the command displays a platform notice: "service management not supported on this platform; showing process info only" and attempts to locate running processes by known binary paths, displaying `<unknown>` for fields that cannot be resolved

### Requirement: Source Path Resolution
The command SHALL resolve each service's source directory by extracting the `WorkingDirectory` field from the systemd unit file. It SHALL cross-validate against any `--project <path>` or binary path argument in `ExecStart` and use `WorkingDirectory` as the authoritative value.

#### Scenario: WorkingDirectory is present in systemd unit
- **WHEN** `systemctl --user show mo-server.service -p FragmentPath` returns a unit file path, and the unit file contains `WorkingDirectory=/home/user/repos/mohist`
- **THEN** the source path displayed is `/home/user/repos/mohist`

#### Scenario: WorkingDirectory is missing but ExecStart contains --project
- **WHEN** the systemd unit has no `WorkingDirectory` but `ExecStart` contains `--project /path/to/project`
- **THEN** the source path SHALL fall back to the project path from `ExecStart`

#### Scenario: Neither WorkingDirectory nor ExecStart provides a project path
- **WHEN** the systemd unit has no `WorkingDirectory` and `ExecStart` does not contain a `--project` flag
- **THEN** the source path SHALL fall back to the directory of the binary in `ExecStart`

### Requirement: Git Commit Identification
The command SHALL read git commit information from each resolved source directory using `git -C <path> rev-parse --short HEAD` for the commit SHA and `git -C <path> log -1 --format=%s` for the commit subject.

#### Scenario: Source directory is a git repository
- **WHEN** the resolved source directory contains a `.git` directory and git commands succeed
- **THEN** the source line displays the directory path, an `@` separator, the short commit SHA, and the commit subject in parentheses

#### Scenario: Git command fails or times out
- **WHEN** `git rev-parse` fails (e.g., no git installed, timeout, permission denied)
- **THEN** the source line displays `<unknown>` for git information without blocking other fields

### Requirement: Runner-to-Server Connectivity Check
The command SHALL verify runner-to-server connectivity by making a single HTTP request to a known server API endpoint (e.g., `/api/projects`). The result SHALL be displayed inline on the runner status line.

#### Scenario: Server is reachable from runner
- **WHEN** the server is active and an HTTP GET to `http://0.0.0.0:3456/api/projects` returns a 2xx response
- **THEN** the runner line appends `server ok` to the status display

#### Scenario: Server is unreachable
- **WHEN** the server is active but the HTTP request fails (connection refused, timeout, non-2xx)
- **THEN** the runner line appends `server unreachable` to the status display

#### Scenario: Server is not running
- **WHEN** the server service is inactive or not installed
- **THEN** the connectivity check SHALL be skipped; no connectivity indicator is shown

### Requirement: CLI Version and Build Metadata
The command SHALL display the CLI version, binary installation path, and build date. Version and build date SHALL be read from embedded assembly metadata (`AssemblyInformationalVersion`).

#### Scenario: CLI binary has embedded version metadata
- **WHEN** the CLI binary includes build-time version and date metadata
- **THEN** the CLI line displays the version string, the resolved binary path, and the build date

#### Scenario: CLI binary path cannot be resolved
- **WHEN** the CLI binary path cannot be determined (e.g., self-executing from a non-standard location)
- **THEN** the CLI line displays `<unknown>` for the binary path but still SHALL show the version if available

### Requirement: Project and Data Directory Info
The command SHALL display the current active project name with total and active issue counts, and the data directory path with total disk usage.

#### Scenario: Active project exists with issues
- **WHEN** the current project has stored issues
- **THEN** the project line displays the project name followed by issue counts in the format `(N issues, M active)`

#### Scenario: No active project or no issues
- **WHEN** no project is configured or the project has zero issues
- **THEN** the project line displays `<no project>` or `(0 issues)` respectively

#### Scenario: Data directory exists and is readable
- **WHEN** the Mohist data directory exists and disk usage can be computed
- **THEN** the data directory line displays the path and total size (e.g., `412 MB`)

#### Scenario: Data directory cannot be read
- **WHEN** the data directory does not exist or disk usage computation fails
- **THEN** the data directory line displays the path followed by `<unknown>` for the size

### Requirement: Verbose Output Mode
When invoked with the `--verbose` flag, the command SHALL append additional sections to the default output: installed skills list with name, version, and install path; git remote origin URL from the source directory; opencode runtime command, version, and available model count; relevant environment variables (`MOHIST_*`, `SERVER_URL`, `RUNNER_ID`, `MAX_CONCURRENT_WORKFLOWS`); OS, architecture, .NET runtime version, and Node.js version; runner active and maximum capacity; and disk usage breakdown by category (projects, logs, worktrees).

#### Scenario: --verbose flag is provided
- **WHEN** `mo info --verbose` is executed
- **THEN** all default output fields are displayed first, followed by each verbose section with a header; any verbose field that cannot be read SHALL display `<unknown>` or be omitted without blocking other verbose fields

#### Scenario: --verbose with skills installed
- **WHEN** skills are installed in the Mohist skills directory
- **THEN** the skills section lists each skill's name, version, and install path on separate lines

#### Scenario: --verbose with git remote available
- **WHEN** the resolved source directory is a git repository with a configured `origin` remote
- **THEN** the git remote section displays the origin URL

### Requirement: JSON Output Mode
When invoked with the `--json` flag, the command SHALL output all collected information as a single JSON object to stdout. The JSON structure SHALL be nested, with field names corresponding one-to-one with the default and verbose output fields. All field names in the JSON output SHALL be stable for programmatic consumption by external skills.

#### Scenario: --json flag produces valid JSON
- **WHEN** `mo info --json` is executed
- **THEN** stdout contains a single valid JSON object with keys for CLI info, server status, runner status, project info, and data directory

#### Scenario: --json with --verbose includes all fields
- **WHEN** `mo info --json --verbose` is executed
- **THEN** the JSON object includes all verbose sections (skills, git remote, opencode runtime, env vars, OS/runtime versions, capacity, disk usage) in addition to default fields

#### Scenario: --json with missing fields
- **WHEN** a field cannot be resolved (e.g., service not running)
- **THEN** the corresponding JSON value SHALL be `null` or an appropriate sentinel string (`"<not running>"`, `"<unknown>"`)

### Requirement: Read-Only Operation
The `mo info` command SHALL be strictly read-only. It MUST NOT modify any files, systemd units, database entries, or configuration. It MUST NOT make external network requests except the local server connectivity check. Information SHALL be gathered exclusively from local filesystem, systemctl, procfs, process inspection, git, and environment variables.

#### Scenario: Command produces no side effects
- **WHEN** `mo info` is executed multiple times in succession
- **THEN** no files, services, or configuration are modified; outputs are idempotent modulo time-dependent fields (uptime, disk usage)

### Requirement: Fast Execution Target
The command SHALL target completion in under 1 second for the default output mode. Individual data collection steps SHALL have timeouts to prevent any single source from delaying the overall output beyond 2 seconds.

#### Scenario: Default output completes within time target
- **WHEN** all data sources are available and responsive
- **THEN** `mo info` (default mode) completes in under 1 second

#### Scenario: Slow data source does not block output
- **WHEN** a single data source (e.g., server HTTP ping) takes longer than 1 second
- **THEN** that field SHALL display `<timeout>` and the rest of the output SHALL be displayed without waiting for the slow source

### Requirement: Non-Overlap with Existing Commands
The `mo info` command SHALL NOT replace or deprecate `mo status`, `mo server status`, or `mo runner status`. It SHALL coexist as a distinct command answering a distinct question: "what is my environment and installation source?" versus the existing commands that answer "what is the project/issue status?" and "what is the raw systemd view?"

#### Scenario: Existing commands remain unchanged
- **WHEN** `mo status`, `mo server status`, or `mo runner status` are executed after `mo info` is implemented
- **THEN** their output format and behavior SHALL be identical to before the introduction of `mo info`
